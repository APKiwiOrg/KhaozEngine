"""Collect one already-running, human-readied schema-4 window. No launch or retry."""
import argparse
from contextlib import contextmanager
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

CAPACITY_WORK_ROOT = Path('/tmp').resolve()/'grimhollow-mac-frame-performance'
HELPER_FILES = ('analyze.py','collect.py','capacity.py','archive.py')

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


@contextmanager
def regular_input(path):
    import archive
    path=Path(path).absolute()
    if path.is_relative_to('/tmp'):
        path=Path('/tmp').resolve()/path.relative_to('/tmp')
    with archive._directory(path.parent) as directory, archive._file(directory,path.name) as descriptor:
        yield descriptor


def read_regular_bytes(path):
    # Startup logs may append. Keep the existing byte-snapshot semantics after safe file admission.
    with regular_input(path) as descriptor, os.fdopen(os.dup(descriptor),'rb') as stream:
        return stream.read()


def digest(path):
    import archive
    h=hashlib.sha256()
    with regular_input(path) as descriptor:
        before=archive._identity(os.fstat(descriptor))
        with os.fdopen(os.dup(descriptor),'rb') as stream:
            for chunk in iter(lambda:stream.read(1024*1024),b''):
                h.update(chunk)
        require(archive._identity(os.fstat(descriptor))==before,'Input changed while hashing')
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
        require(integer(m['runtimeFileCount'],644,644)==644 and integer(m['contentFileCount'],395,395)==395,
                'Frozen inventory counts changed')
        if 'worldSelection' in m:
            from capacity import validate_world_manifest
            validate_world_manifest(m)
        else:
            require(len(m['world'])==41,'Legacy world row count changed')
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
        path=client/name; canonical_owned(path,client); before=stat_key(path); st=path.stat()
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
        path=engine/relative(row['path']);canonical_owned(path,engine)
        committed=subprocess.check_output(['git','-C',str(engine),'show',manifest['probeCommit']+':'+row['path']],timeout=30)
        value=digest(path)
        require(value==row['sha256']==hashlib.sha256(committed).hexdigest(), 'Probe source identity changed')
        rows.append(dict(path=row['path'],sha256=value))
    game=Path(manifest['gameRoot']).resolve(strict=True)
    require(bool(manifest['gameSourceHashes']), 'Game source hashes missing')
    for row in manifest['gameSourceHashes']:
        path=game/relative(row['path']);canonical_owned(path,game)
        committed=subprocess.check_output(['git','-C',str(game),'show',manifest['gameCommit']+':'+row['path']],timeout=30)
        require(digest(path)==row['sha256']==hashlib.sha256(committed).hexdigest(), 'Game source identity changed')
    for row in manifest['replacements']:
        source=Path(row['source']);canonical_owned(source,engine)
        require(digest(source)==row['sha256'], 'Committed replacement binary changed')
        client=Path(manifest['client'])/(row['name']+'.dll');canonical_owned(client,Path(manifest['client']))
        require(digest(client)==row['sha256'] and source.stat().st_ino!=client.stat().st_ino, 'Replacement payload mismatch')
    return rows


def verify_world(manifest):
    root=Path(manifest['gameRoot']).resolve(strict=True)
    if 'worldSelection' in manifest:
        import archive
        from capacity import CATALOG_PATHS, WORLD_ROOT, validate_world_manifest
        selection=validate_world_manifest(manifest)
        directory=root/WORLD_ROOT
        with archive._directory(directory): pass
        expected={row['path'] for row in manifest['world']}
        expected.update(str(parent) for row in manifest['world'] for parent in Path(row['path']).parents
                        if str(parent).startswith(WORLD_ROOT+'/'))
        actual=set()
        for parent,directories,files in os.walk(directory,followlinks=False):
            actual.update(str((Path(parent)/name).relative_to(root)) for name in directories+files)
        require(actual==expected,'Working world inventory changed')
        sources={row['path']:row['sha256'] for row in manifest['gameSourceHashes']}
        rows=manifest['world']+[dict(path=path,sha256=sources[path]) for path in CATALOG_PATHS]
        with archive._directory(root) as parent:
            for row in rows:
                raw,_=archive._read(parent,row['path'])
                require(hashlib.sha256(raw).hexdigest()==row['sha256'],'World or catalog input changed')
        return selection
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


def helper_hashes():
    return {name:digest(Path(__file__).with_name(name)) for name in HELPER_FILES}


def metadata_bytes(record):
    return (json.dumps(record,indent=2)+'\n').encode('utf-8')


def archive_outcome(args,root,preparation,result,metadata,audit_path,record,*,metadata_error=None):
    import archive
    from capacity import decode_startup
    measurement={'collected-and-audited':'valid','limited-retain-evidence':'limited'}.get(record['status'],'failed')
    claim_written=False
    try:
        identity_path=result.with_suffix('.identity.json')
        startup_path=result.with_suffix('.startup-proof.log')
        claim_path=result.with_suffix('.archive-claim.json')
        receipt_path=result.with_suffix('.archive-receipt.json')
        sources=dict(probeCommit=record.get('probeCommit'),gameCommit=record.get('gameCommit'),
                     frozenEngineBase=record.get('frozenEngineBase'),sourceHashes=record.get('sourceHashes'),
                     helperHashes=record.get('helperHashes'),actualHelperHashes=helper_hashes(),
                     metalAssembly=record.get('metalAssembly'),codeIdentities=record.get('codeIdentities'),
                     worldSelection=record.get('worldSelection'))
        with archive._directory(root) as directory:
            if metadata_error is not None:
                # Retain both the old claim and any incomplete temporary file. Recovery uses a new,
                # exclusive copy of the exact pending record, never a second metadata replacement.
                metadata=result.with_suffix('.collection.recovery.json')
                archive._write(directory,metadata.name,metadata_bytes(record))
            archive._write(directory,identity_path.name,archive._json(sources))
            startup=None
            if record.get('startupProof') is not None:
                raw,_=decode_startup(record['startupProof'])
                archive._write(directory,startup_path.name,raw)
                startup=startup_path.name
            entries=dict(raw=result.name,collection=metadata.name,analysis=audit_path.name,
                         preparation=preparation.name,startup=startup,identity=identity_path.name)
            hashes={}
            for name,path in list(entries.items()):
                if path is None: continue
                known={'raw':'resultSha256','analysis':'analysisSha256','preparation':'preparationSha256'}.get(name)
                try: raw,_=archive._read(directory,path)
                except FileNotFoundError:
                    require(measurement=='failed' and name in ('raw','analysis') and record.get(known) is None,
                            'Previously captured '+name+' is missing before archival')
                    entries[name]=None
                    continue
                require(name!='collection' or raw==metadata_bytes(record),
                        'Final collection differs from the audited record')
                value=hashlib.sha256(raw).hexdigest()
                require(known is None or record.get(known) is None or record[known]==value,
                        'Final '+name+' differs from recorded capture hash')
                hashes[name]=dict(sha256=value,bytes=len(raw))
            commit=record.get('probeCommit')
            if commit is None:
                raw,_=archive._read(directory,preparation.name)
                commit=json.loads(raw)['probeCommit']
            destination=archive.destination_for(commit,args.run_id)
            claim=dict(schemaVersion=1,probeCommit=commit,runId=args.run_id,collectionId=record['collectionId'],
                       sourceRoot=str(root),destination=str(destination),entries=entries,expectedHashes=hashes,
                       measurementStatus=measurement)
            if metadata_error is not None:
                claim['metadataFinalizationError']=dict(type=type(metadata_error).__name__,message=str(metadata_error))
            archive._write(directory,claim_path.name,archive._json(claim))
            claim_written=True
        if metadata_error is not None:
            raise metadata_error
        receipt=archive.publish_bundle(source_root=root,destination=destination,entries=entries,
                                       measurement_status=measurement,expected_hashes=hashes)
        with archive._directory(root) as directory:
            archive._write(directory,receipt_path.name,archive._json(receipt))
        print(json.dumps(dict(status=record['status'],measurementStatus=measurement,archive=receipt,
                              metadata=str(metadata),result=str(result),claim=str(claim_path),receipt=str(receipt_path))))
        return {'valid':0,'limited':2,'failed':1}[measurement]
    except BaseException as error:
        captured=record.get('triggerCreated') is True or result.exists()
        print(json.dumps(dict(status='captured-but-not-archived' if captured else 'failed-not-archived',
                              measurementStatus=measurement,archivalStatus='failed',errorType=type(error).__name__,
                              error=str(error),metadata=str(metadata),claim=str(claim_path) if claim_written else None,
                              metadataFinalizationError=None if metadata_error is None else
                              dict(type=type(metadata_error).__name__,message=str(metadata_error)))))
        return 3


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--preparation',type=Path,required=True)
    parser.add_argument('--ready-process-id',type=int,required=True)
    parser.add_argument('--mode',choices=['counts','timings'],required=True)
    parser.add_argument('--result',type=Path,required=True)
    parser.add_argument('--expected-recording-slots',type=int,choices=[3,6])
    parser.add_argument('--startup-log',type=Path)
    parser.add_argument('--run-id')
    args=parser.parse_args()
    require((args.expected_recording_slots is None)==(args.startup_log is None),
            'Recording-slot expectation and owned startup log are a paired opt-in')
    require(args.expected_recording_slots is None or args.mode=='counts','Capacity comparison requires counts mode')
    require((args.expected_recording_slots is None)==(args.run_id is None),'Capacity mode requires an archival run identifier')
    if args.expected_recording_slots is not None:
        import archive
        archive.destination_for('0'*40,args.run_id)
    os.umask(0o077)
    preparation=args.preparation.resolve(strict=True)
    root=preparation.parent
    result=canonical_owned(args.result,root)
    require(result.parent==root and result.suffix=='.json','Result must be a direct child JSON path')
    if args.expected_recording_slots is not None:
        require(root.is_relative_to(CAPACITY_WORK_ROOT),'Capacity work must remain inside the approved temporary root')
        # Canonicalize only the known system /tmp alias, then refuse every other symlink component.
        for path in (args.preparation,args.result,args.startup_log):
            absolute=path.absolute()
            if absolute.is_relative_to('/tmp'):
                absolute=Path('/tmp').resolve()/absolute.relative_to('/tmp')
            with archive._directory(absolute.parent): pass
            require(not absolute.is_symlink(),'Capacity evidence input is symlinked')
    trigger=Path(str(result)+'.start')
    metadata=result.with_suffix('.collection.json')
    temporary=metadata.with_suffix('.tmp')
    audit_path=result.with_suffix('.audit.json')
    require(all(not p.exists() and not p.is_symlink() for p in (result,trigger,metadata,temporary,audit_path)),
            'Use fresh result, start and evidence paths')
    if args.expected_recording_slots is not None:
        extra=[result.with_suffix(suffix) for suffix in
               ('.identity.json','.startup-proof.log','.archive-claim.json','.archive-receipt.json',
                '.collection.recovery.json')]
        require(all(not p.exists() and not p.is_symlink() for p in extra),'Use fresh archival evidence paths')
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

    save_error=None
    def save():
        nonlocal save_error
        save_error=None
        try:
            with temporary.open('xb') as stream:
                stream.write(metadata_bytes(record))
            os.replace(temporary,metadata)
        except BaseException as error:
            save_error=error
            raise

    try:
        preparation_raw=read_regular_bytes(preparation)
        manifest=json.loads(preparation_raw)
        validate_preparation(manifest)
        client=Path(manifest['client']).resolve(strict=True)
        require(client==root/'client','Preparation does not own this client directory')
        record.update(preparationSha256=hashlib.sha256(preparation_raw).hexdigest(),probeCommit=manifest['probeCommit'],gameCommit=manifest['gameCommit'],
                      requiredClosure=sorted(REQUIRED_CLOSURE),codeIdentities=dict(app=manifest['appCodeIdentity'],engine=manifest['engineCodeIdentity']))
        if args.expected_recording_slots is not None:
            from capacity import ENGINE_BASE, GAME_BASE, select_startup, validate_startup, validate_world_manifest
            require(manifest['frozenEngineBase']==ENGINE_BASE and manifest['gameCommit']==GAME_BASE,'Frozen capacity source differs')
            validate_world_manifest(manifest)
            startup_path=canonical_owned(args.startup_log,root)
            require(startup_path.parent==root and not args.startup_log.is_symlink(),'Startup log must be owned and direct')
            selected=select_startup(read_regular_bytes(startup_path))
            record.update(startupProof=selected,helperHashes=helper_hashes())
            metal=next(x for x in manifest['replacements'] if x['name']=='KhaozEngine.Gpu.Metal')
            validate_startup(selected,metal,args.expected_recording_slots,manifest['probeCommit'])
            declared_sources={row['path']:row['sha256'] for row in manifest['sourceHashes']}
            require(all(declared_sources.get('scripts/mac-frame-ring-waits/'+name)==value
                        for name,value in record['helperHashes'].items()),'Committed helper source identity differs')
            record.update(expectedRecordingSlots=args.expected_recording_slots,frozenEngineBase=ENGINE_BASE,
                          startupProof=selected,metalAssembly={k:metal[k] for k in
                          ('name','mvid','assemblyVersion','informationalVersion','sha256')})
        record['processBefore']=identity(pid,client)
        payload=verify_payload(manifest)
        record.update(payloadBefore=payload,runtimeFilesRehashed=len(payload),contentFiles=395)
        record['sourceHashes']=verify_sources(manifest)
        world_selection=verify_world(manifest)
        record['worldFilesRehashed']=len(manifest['world'])
        if world_selection is not None: record['worldSelection']=world_selection
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
        require(verify_world(manifest)==world_selection,'World selection changed during capture')
        require(digest(preparation)==record['preparationSha256'],'Preparation changed during capture')
        before_files={row['path']:row for row in payload}
        after_files={row['path']:row for row in record['payloadAfter']}
        critical=sorted(name+'.dll' for name in REQUIRED_CLOSURE|{'Grimhollow'})
        record['runtimeFiles']=[dict(before_files[name],afterSha256=after_files[name]['sha256'],afterStat=after_files[name]['stat']) for name in critical]
        result_stat=stat_key(result)
        raw=read_regular_bytes(result)
        require(stat_key(result)==result_stat,'Result changed while being read')
        record['resultSha256']=hashlib.sha256(raw).hexdigest()
        if args.expected_recording_slots is not None:
            require(helper_hashes()==record['helperHashes'],'Collection helpers changed during capture')
            require(select_startup(read_regular_bytes(startup_path))==record['startupProof'],'Startup proof changed during capture')
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
    except BaseException as error:
        if args.expected_recording_slots is None and not isinstance(error,Exception): raise
        if args.expected_recording_slots is not None and save_error is not None:
            return archive_outcome(args,root,preparation,result,metadata,audit_path,record,metadata_error=save_error)
        record.update(status='failed-retain-evidence',finishedAtUtc=now(),errorType=type(error).__name__,error=str(error))
        try:
            save()
        except BaseException as metadata_error:
            if args.expected_recording_slots is None: raise
            return archive_outcome(args,root,preparation,result,metadata,audit_path,record,metadata_error=metadata_error)
        if args.expected_recording_slots is None:
            print(json.dumps(dict(status=record['status'],error=str(error),metadata=str(metadata))))
            return 1

    # Archival cannot return to capture failure handling and rewrite finalized claim inputs.
    if args.expected_recording_slots is not None:
        return archive_outcome(args,root,preparation,result,metadata,audit_path,record)
    print(json.dumps(dict(status=record['status'],metadata=str(metadata),result=str(result))))
    return {'valid':0,'limited':2,'invalid':1}[audit['status']]


if __name__=='__main__':
    raise SystemExit(main())
