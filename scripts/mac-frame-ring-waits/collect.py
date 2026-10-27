"""Collect one already-running, human-readied schema-4 window. No launch or retry."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import stat
import subprocess
import time
import uuid
from analyze import INT_MAX, REQUIRED_CLOSURE, analyse, guid, integer

REQUIRED_EDGES = {
    'KhaozEngine.Gpu': set(),
    'KhaozEngine.Gpu.Metal': {'KhaozEngine.Gpu'},
    'KhaozEngine.Windowing': {'KhaozEngine.Gpu', 'KhaozEngine.Gpu.Metal'},
    'KhaozEngine.Render3D': {'KhaozEngine.Gpu', 'KhaozEngine.Windowing'},
    'KhaozEngine.Game.Render3D': {'KhaozEngine.Render3D', 'KhaozEngine.Windowing'},
}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(path):
    h=hashlib.sha256()
    with Path(path).open('rb') as stream:
        for chunk in iter(lambda:stream.read(1024*1024),b''):
            h.update(chunk)
    return h.hexdigest()


def canonical_owned(path, root):
    root=Path(root).resolve(strict=True)
    resolved=Path(path).resolve()
    require(resolved != root and resolved.is_relative_to(root), 'Path escapes owned directory')
    return resolved


def relative(path):
    p=Path(path)
    require(not p.is_absolute() and '..' not in p.parts and str(p) not in ('.',''), 'Invalid relative inventory path')
    return str(p)


def validate_preparation(manifest):
    try:
        m=manifest
        require(m['status']=='prepared-and-audited', 'Preparation has not been audited')
        for key in ('probeCommit','gameCommit'):
            require(re.fullmatch('[0-9a-f]{40}',m[key]) is not None, 'Missing exact code commit')
        require(integer(m['runtimeFileCount'],644,644)==644 and integer(m['contentFileCount'],395,395)==395
                and len(m['world'])==41, 'Frozen inventory counts changed')
        replacements=m['replacements']; names=[x['name'] for x in replacements]
        require(set(names)==REQUIRED_CLOSURE and len(names)==5, 'Require exactly five replacement DLLs')
        for row in replacements:
            require(row['assemblyVersion']=='21.13.0.0' and row['fileVersion']=='21.13.0.0', 'Assembly version changed')
            require(row['informationalVersion']=='21.13.0+'+m['probeCommit'], 'Replacement committed-source stamp mismatch')
            guid(row['mvid'])
            require(REQUIRED_EDGES[row['name']] <= {x['name'] for x in row['references']}, 'Required runtime dependency edge missing')
            require(all(x['version']=='21.13.0.0' for x in row['references'] if x['name'].startswith('KhaozEngine.')), 'Engine reference version mismatch')
            require(re.fullmatch('[0-9a-f]{64}',row['sha256']) is not None, 'Invalid replacement hash')
        for kind in ('clientFiles','world','sourceHashes'):
            paths=[relative(x['path']) for x in m[kind]]
            require(bool(paths) and len(set(paths))==len(paths), 'Missing or duplicate '+kind)
            require(all(re.fullmatch('[0-9a-f]{64}',x['sha256']) is not None for x in m[kind]), 'Invalid '+kind+' hash')
        for row in m['clientFiles']:integer(row['bytes'])
        for key,name,commit in (('appCodeIdentity','Grimhollow','gameCommit'),('engineCodeIdentity','KhaozEngine.Windowing','probeCommit')):
            value=m[key];guid(value['mvid'])
            require(value['name']==name and value['informationalVersion'].endswith('+'+m[commit]), 'Code identity mismatch')
        engine=next(x for x in replacements if x['name']=='KhaozEngine.Windowing')
        require(guid(engine['mvid'])==guid(m['engineCodeIdentity']['mvid'])
                and engine['informationalVersion']==m['engineCodeIdentity']['informationalVersion'], 'Recorder identity mismatch')
        return set(names)
    except (KeyError,TypeError,AttributeError) as error:
        raise ValueError('Invalid preparation shape') from error


def stat_key(path):
    value=path.stat()
    return [value.st_dev,value.st_ino,value.st_size,value.st_mtime_ns,value.st_ctime_ns]


def verify_payload(manifest):
    client=Path(manifest['client']).resolve(strict=True)
    expected={relative(x['path']):x for x in manifest['clientFiles']}
    require(len(expected)==644 and len(expected)==len(manifest['clientFiles']), 'Require all 644 payload rows')
    paths=list(client.rglob('*'))
    require(not any(x.is_symlink() for x in paths), 'Payload contains a symlink')
    actual={str(x.relative_to(client)) for x in paths if x.is_file()}
    require(actual==set(expected), 'Payload inventory changed')
    require(sum(x.startswith('Content/') for x in actual)==395, 'Content inventory changed')
    result=[]; inodes=set()
    for name,row in sorted(expected.items()):
        path=canonical_owned(client/name,client); before=stat_key(path); st=path.stat()
        require(st.st_nlink==1 and (st.st_dev,st.st_ino) not in inodes, 'Payload is hardlinked')
        inodes.add((st.st_dev,st.st_ino))
        require(before[2]==row['bytes'] and oct(stat.S_IMODE(st.st_mode))==row['mode'], 'Payload size or mode changed')
        value=digest(path)
        require(value==row['sha256'] and stat_key(path)==before, 'Payload hash changed '+name)
        result.append(dict(path=name,sha256=value,stat=before))
    return result


def run_text(argv):
    return subprocess.check_output(argv,text=True,timeout=30).strip()


def verify_sources(manifest):
    engine=Path(manifest['probeRoot']).resolve(strict=True)
    require(run_text(['git','-C',str(engine),'rev-parse','HEAD'])==manifest['probeCommit'], 'Probe source moved')
    require(not run_text(['git','-C',str(engine),'status','--porcelain=v1']), 'Probe source is dirty')
    rows=[]
    for row in manifest['sourceHashes']:
        path=canonical_owned(engine/relative(row['path']),engine)
        committed=subprocess.check_output(['git','-C',str(engine),'show',manifest['probeCommit']+':'+row['path']],timeout=30)
        value=digest(path)
        require(value==row['sha256']==hashlib.sha256(committed).hexdigest(), 'Probe source identity changed')
        rows.append(dict(path=row['path'],sha256=value))
    game=Path(manifest['gameRoot']).resolve(strict=True)
    require(bool(manifest['gameSourceHashes']), 'Game source hashes missing')
    for row in manifest['gameSourceHashes']:
        path=canonical_owned(game/relative(row['path']),game)
        committed=subprocess.check_output(['git','-C',str(game),'show',manifest['gameCommit']+':'+row['path']],timeout=30)
        require(digest(path)==row['sha256']==hashlib.sha256(committed).hexdigest(), 'Game source identity changed')
    for row in manifest['replacements']:
        source=canonical_owned(row['source'],engine)
        require(digest(source)==row['sha256'], 'Committed replacement binary changed')
        client=canonical_owned(Path(manifest['client'])/(row['name']+'.dll'),Path(manifest['client']))
        require(digest(client)==row['sha256'] and source.stat().st_ino!=client.stat().st_ino, 'Replacement payload mismatch')
    return rows


def verify_world(manifest):
    root=Path(manifest['gameRoot']).resolve(strict=True)
    for row in manifest['world']:
        require(digest(canonical_owned(root/relative(row['path']),root))==row['sha256'], 'World input changed')


def identity(pid, client):
    raw=run_text(['ps','-p',str(pid),'-o','pid=,lstart=,command='])
    match=re.fullmatch(r'\s*(\d+)\s+(\w{3}\s+\w{3}\s+\d{1,2}\s+\d{2}:\d{2}:\d{2}\s+\d{4})\s+(.+)',raw)
    require(match is not None and int(match[1])==pid, 'PID/start identity unavailable')
    args=shlex.split(match[3])
    require(len(args)==3 and args[0]=='dotnet' and args[2]=='--with-server', 'Unexpected exact client command')
    require(Path(args[1]).resolve()==(client/'Grimhollow.dll').resolve(), 'Client command targets another payload')
    return raw


def mappings(pid, client):
    output=run_text(['lsof','-p',str(pid),'-Fn'])
    mapped={str(Path(x[1:]).resolve()) for x in output.splitlines() if x.startswith('n') and x.endswith('.dll')}
    expected={str((client/(name+'.dll')).resolve()) for name in REQUIRED_CLOSURE|{'Grimhollow'}}
    require(expected<=mapped, 'A required critical DLL is not mapped from this payload')
    # Also refuse a second location with the same critical filename.
    require({x for x in mapped if Path(x).name in {Path(y).name for y in expected}}==expected, 'Foreign critical DLL mapping')
    return sorted(expected)


def foreground():
    code='ObjC.import("AppKit"); var a=$.NSWorkspace.sharedWorkspace.frontmostApplication; JSON.stringify({pid:Number(a.processIdentifier),name:ObjC.unwrap(a.localizedName)});'
    return json.loads(run_text(['osascript','-l','JavaScript','-e',code]))


def now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--preparation',type=Path,required=True)
    parser.add_argument('--ready-process-id',type=int,required=True)
    parser.add_argument('--mode',choices=['counts','timings'],required=True)
    parser.add_argument('--result',type=Path,required=True)
    parser.add_argument('--expected-recording-slots',type=int,choices=[3,6])
    parser.add_argument('--startup-log',type=Path)
    args=parser.parse_args()
    require((args.expected_recording_slots is None)==(args.startup_log is None),
            'Recording-slot expectation and owned startup log are a paired opt-in')
    require(args.expected_recording_slots is None or args.mode=='counts','Capacity comparison requires counts mode')
    os.umask(0o077)
    preparation=args.preparation.resolve(strict=True)
    root=preparation.parent
    result=canonical_owned(args.result,root)
    require(result.parent==root and result.suffix=='.json','Result must be a direct child JSON path')
    trigger=Path(str(result)+'.start')
    metadata=result.with_suffix('.collection.json')
    temporary=metadata.with_suffix('.tmp')
    audit_path=result.with_suffix('.audit.json')
    require(all(not p.exists() and not p.is_symlink() for p in (result,trigger,metadata,temporary,audit_path)),
            'Use fresh result, start and evidence paths')
    pid=args.ready_process_id
    integer(pid,2,INT_MAX)
    record=dict(status='preflight',startedAtUtc=now(),collectionId=str(uuid.uuid4()),ownershipReserved=True,
                preflightRechecked=False,triggerConsumed=False,processId=pid,expectedMode=args.mode,result=str(result),
                trigger=str(trigger),requestedSeconds=10,boundedWaitSeconds=45,triggerCreated=False,freshPaths=True,
                continuousFocusTelemetry='Engine interval focus state and OS endpoints, no continuous OS focus observer')

    # Metadata is also the permanent claim. A loser exits outside the owner's failure/save path.
    # Even an interruption before the JSON finishes leaves the path reserved for diagnosis.
    with metadata.open('x') as stream:
        json.dump(record,stream,indent=2)
        stream.write('\n')

    def save():
        with temporary.open('x') as stream:
            json.dump(record,stream,indent=2)
            stream.write('\n')
        os.replace(temporary,metadata)

    try:
        manifest=json.loads(preparation.read_text())
        validate_preparation(manifest)
        client=Path(manifest['client']).resolve(strict=True)
        require(client==root/'client','Preparation does not own this client directory')
        record.update(preparationSha256=digest(preparation),probeCommit=manifest['probeCommit'],gameCommit=manifest['gameCommit'],
                      requiredClosure=sorted(REQUIRED_CLOSURE),codeIdentities=dict(app=manifest['appCodeIdentity'],engine=manifest['engineCodeIdentity']))
        if args.expected_recording_slots is not None:
            from capacity import ENGINE_BASE, GAME_BASE, select_startup, validate_startup
            require(manifest['frozenEngineBase']==ENGINE_BASE and manifest['gameCommit']==GAME_BASE,'Frozen capacity source differs')
            startup_path=canonical_owned(args.startup_log,root)
            require(startup_path.parent==root and not args.startup_log.is_symlink(),'Startup log must be owned and direct')
            selected=select_startup(startup_path.read_bytes())
            metal=next(x for x in manifest['replacements'] if x['name']=='KhaozEngine.Gpu.Metal')
            validate_startup(selected,metal,args.expected_recording_slots,manifest['probeCommit'])
            record.update(expectedRecordingSlots=args.expected_recording_slots,frozenEngineBase=ENGINE_BASE,
                          startupProof=selected,metalAssembly={k:metal[k] for k in
                          ('name','mvid','assemblyVersion','informationalVersion','sha256')})
        record['processBefore']=identity(pid,client)
        payload=verify_payload(manifest)
        record.update(payloadBefore=payload,runtimeFilesRehashed=len(payload),contentFiles=395)
        record['sourceHashes']=verify_sources(manifest)
        verify_world(manifest)
        record['worldFilesRehashed']=41
        record['mappedBefore']=mappings(pid,client)
        require(identity(pid,client)==record['processBefore'],'Process changed during preflight')
        record['foregroundBefore']=foreground()
        require(record['foregroundBefore']['pid']==pid,'Client is not foreground, no trigger created')
        require(all(not p.exists() and not p.is_symlink() for p in (result,trigger,temporary,audit_path)),
                'Capture paths changed during preflight, no trigger created')
        record['preflightRechecked']=True
        save()
        require(all(not p.exists() and not p.is_symlink() for p in (result,trigger,temporary,audit_path)),
                'Capture paths changed before trigger creation')
        # This is the only trigger creation. Any later failure retains evidence and ends the command.
        with trigger.open('xb') as stream:
            created=os.fstat(stream.fileno())
            record['triggerIdentity']=[created.st_dev,created.st_ino]
        record.update(triggerCreated=True,triggeredAtUtc=now(),status='sampling')
        save()
        deadline=time.monotonic()+45
        while True:
            if not trigger.exists() and not trigger.is_symlink() and not record['triggerConsumed']:
                record.update(triggerConsumed=True,triggerConsumedAtUtc=now())
                save()
            if result.exists() or result.is_symlink() or time.monotonic()>=deadline:
                break
            time.sleep(.25)
        require(result.is_file() and not result.is_symlink(),'No result before timeout, do not repeat automatically')
        record['resultObservedAtUtc']=now()
        require(record['triggerConsumed'] and not trigger.exists() and not trigger.is_symlink(),
                'Result appeared without consumption of this collection trigger')
        record['processAfter']=identity(pid,client)
        record['foregroundAfter']=foreground()
        require(record['processAfter']==record['processBefore'] and record['foregroundAfter']['pid']==pid,'Process/focus endpoint changed')
        record['mappedAfter']=mappings(pid,client)
        record['payloadAfter']=verify_payload(manifest)
        require(record['payloadAfter']==payload,'Payload bytes, modes or identities changed')
        require(verify_sources(manifest)==record['sourceHashes'],'Source changed during capture')
        verify_world(manifest)
        require(digest(preparation)==record['preparationSha256'],'Preparation changed during capture')
        before_files={row['path']:row for row in payload}
        after_files={row['path']:row for row in record['payloadAfter']}
        critical=sorted(name+'.dll' for name in REQUIRED_CLOSURE|{'Grimhollow'})
        record['runtimeFiles']=[dict(before_files[name],afterSha256=after_files[name]['sha256'],afterStat=after_files[name]['stat']) for name in critical]
        result_stat=stat_key(result)
        raw=result.read_bytes()
        require(stat_key(result)==result_stat,'Result changed while being read')
        record['resultSha256']=hashlib.sha256(raw).hexdigest()
        audit=analyse(json.loads(raw),1 if args.mode=='counts' else 2,pid,record,
                      expected_recording_slots=args.expected_recording_slots)
        audit['inputSha256']=record['resultSha256']
        with audit_path.open('x') as stream:
            json.dump(audit,stream,indent=2)
            stream.write('\n')
        record.update(analysisSha256=digest(audit_path),analysisHelperSha256=digest(Path(__file__).with_name('analyze.py')),
                      collectionHelperSha256=digest(Path(__file__)),analysisStatus=audit['status'],finishedAtUtc=now())
        record['status']={'valid':'collected-and-audited','limited':'limited-retain-evidence','invalid':'failed-retain-evidence'}[audit['status']]
        save()
        print(json.dumps(dict(status=record['status'],metadata=str(metadata),result=str(result))))
        return {'valid':0,'limited':2,'invalid':1}[audit['status']]
    except Exception as error:
        record.update(status='failed-retain-evidence',finishedAtUtc=now(),errorType=type(error).__name__,error=str(error))
        save()
        print(json.dumps(dict(status=record['status'],error=str(error),metadata=str(metadata))))
        return 1


if __name__=='__main__':
    raise SystemExit(main())
