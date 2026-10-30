"""Allowlisted immutable local evidence bundles, with no capture entry point."""
import argparse
from contextlib import contextmanager
import ctypes
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import sys
import uuid

ARTIFACTS = {'raw':'result.json','collection':'collection.json','analysis':'analysis.json',
             'preparation':'preparation.json','startup':'startup-proof.log','identity':'helper-source-identity.json'}
ARCHIVE_ROOT = Path.home()/'.local/share/grimhollow-performance-evidence/ring-capacity'
STATES = {'valid','limited','failed'}
DIRECTORY = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW


def _require(condition,message):
    if not condition: raise ValueError(message)


def _json(value): return json.dumps(value,sort_keys=True,separators=(',',':')).encode()+b'\n'


def _unique(pairs):
    result={}
    for key,value in pairs:
        _require(key not in result,'Duplicate manifest key')
        result[key]=value
    return result


def _relative(value):
    _require(type(value) is str and bool(value) and value==str(PurePosixPath(value))
             and not PurePosixPath(value).is_absolute() and '..' not in PurePosixPath(value).parts
             and value!='.' and '\x00' not in value,'Unsafe relative artifact path')
    return value


@contextmanager
def _directory(path,create=False):
    path=Path(path)
    _require(path.is_absolute() and '..' not in path.parts,'Require an absolute directory without traversal')
    fd=os.open('/',DIRECTORY)
    try:
        for component in path.parts[1:]:
            if create:
                try: os.mkdir(component,0o700,dir_fd=fd)
                except FileExistsError: pass
            child=os.open(component,DIRECTORY,dir_fd=fd)
            os.close(fd);fd=child
        yield fd
    finally: os.close(fd)


@contextmanager
def _file(root,relative):
    parts=PurePosixPath(_relative(relative)).parts
    parent=os.dup(root)
    try:
        for part in parts[:-1]:
            child=os.open(part,DIRECTORY,dir_fd=parent);os.close(parent);parent=child
        fd=os.open(parts[-1],os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK,dir_fd=parent)
        try:
            info=os.fstat(fd)
            _require(stat.S_ISREG(info.st_mode) and info.st_nlink==1,'Artifact must be a regular unlinked file')
            yield fd
        finally: os.close(fd)
    finally: os.close(parent)


def _identity(info):
    return (info.st_dev,info.st_ino,info.st_size,info.st_mtime_ns,info.st_ctime_ns)


def _read(root,relative):
    with _file(root,relative) as fd:
        before=_identity(os.fstat(fd))
        with os.fdopen(os.dup(fd),'rb') as stream: raw=stream.read()
        _require(before==_identity(os.fstat(fd)),'Artifact changed during read')
    return raw,before


def _check(raw,expected):
    _require(type(expected) is dict and set(expected)=={'sha256','bytes'},'Invalid expected hash record')
    _require(type(expected['bytes']) is int and expected['bytes']>=0 and len(raw)==expected['bytes'],
             'Artifact byte size differs')
    _require(type(expected['sha256']) is str and re.fullmatch('[0-9a-f]{64}',expected['sha256'])
             and hashlib.sha256(raw).hexdigest()==expected['sha256'],'Artifact hash differs')


def _write(directory,name,raw):
    fd=os.open(name,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600,dir_fd=directory)
    with os.fdopen(fd,'wb') as stream:
        stream.write(raw);stream.flush();os.fsync(stream.fileno())


def _copy_entry(source,relative,temporary,target,expected):
    raw,identity=_read(source,relative);_check(raw,expected)
    _write(temporary,target,raw)
    copied,_=_read(temporary,target);_check(copied,expected)
    return identity


def _manifest(entries,expected_hashes,measurement_status):
    _require(measurement_status in STATES,'Invalid measurement status')
    _require(type(entries) is dict and set(entries)==set(ARTIFACTS),'Unexpected semantic artifact inventory')
    present={key:value for key,value in entries.items() if value is not None}
    _require(measurement_status!='valid' or set(present)==set(ARTIFACTS),'Valid measurement requires every artifact')
    _require(set(expected_hashes)==set(present),'Hash inventory differs from present artifacts')
    _require(len(set(present.values()))==len(present),'Artifact paths must be distinct')
    for value in present.values(): _relative(value)
    return dict(schemaVersion=1,measurementStatus=measurement_status,
                artifacts={key:dict(path=ARTIFACTS[key],**expected_hashes[key]) for key in sorted(present)},
                missing=sorted(set(ARTIFACTS)-set(present)))


def _verify_at(directory):
    raw,_=_read(directory,'manifest.json')
    manifest=json.loads(raw,object_pairs_hook=_unique)
    _require(type(manifest) is dict and set(manifest)=={'schemaVersion','measurementStatus','artifacts','missing'}
             and type(manifest['schemaVersion']) is int and manifest['schemaVersion']==1,'Invalid manifest schema')
    _require(manifest['measurementStatus'] in STATES and type(manifest['artifacts']) is dict
             and type(manifest['missing']) is list,'Invalid manifest status or inventory')
    artifacts=manifest['artifacts'];missing=manifest['missing']
    _require(manifest['measurementStatus']!='valid' or not missing,'Valid bundle is missing artifacts')
    _require(set(artifacts)|set(missing)==set(ARTIFACTS) and not set(artifacts)&set(missing)
             and missing==sorted(set(missing)),'Manifest semantic inventory differs')
    inventory={'manifest.json'}
    for name,row in artifacts.items():
        _require(type(row) is dict and set(row)=={'path','sha256','bytes'} and row['path']==ARTIFACTS[name],
                 'Manifest artifact name differs')
        content,_=_read(directory,row['path']);_check(content,{key:row[key] for key in ('sha256','bytes')})
        inventory.add(row['path'])
    _require(set(os.listdir(directory))==inventory,'Bundle contains unexpected files')
    _require(raw==_json(manifest),'Manifest is not the immutable canonical representation')
    return manifest,hashlib.sha256(raw).hexdigest()


def verify_bundle(destination):
    with _directory(destination) as directory:
        manifest,_=_verify_at(directory)
    return manifest


def _publish_noreplace(parent,temporary,destination):
    # Apple SDK sys/stdio.h declares renameatx_np and RENAME_EXCL=0x00000004.
    # The directory fd anchors both names, so substituted ancestor symlinks cannot redirect publication.
    if sys.platform!='darwin': raise NotImplementedError('Atomic archive publication requires macOS renameatx_np')
    libc=ctypes.CDLL(None,use_errno=True)
    rename=libc.renameatx_np
    rename.argtypes=[ctypes.c_int,ctypes.c_char_p,ctypes.c_int,ctypes.c_char_p,ctypes.c_uint]
    rename.restype=ctypes.c_int
    if rename(parent,os.fsencode(temporary),parent,os.fsencode(destination),4)!=0:
        code=ctypes.get_errno()
        raise OSError(code,os.strerror(code),destination)


def publish_bundle(*,source_root,destination,entries,measurement_status,expected_hashes):
    if sys.platform!='darwin': raise NotImplementedError('No supported atomic no-replace primitive on this platform')
    manifest=_manifest(entries,expected_hashes,measurement_status)
    destination=Path(destination)
    _relative(destination.name)
    with _directory(source_root) as source, _directory(destination.parent,create=True) as parent:
        try:
            os.stat(destination.name,dir_fd=parent,follow_symlinks=False)
        except FileNotFoundError: pass
        else: raise FileExistsError('Archive destination already exists')
        temporary='.'+destination.name+'.publishing-'+uuid.uuid4().hex
        os.mkdir(temporary,0o700,dir_fd=parent)
        published=False
        temp=os.open(temporary,DIRECTORY,dir_fd=parent)
        try:
            identities={}
            for name in sorted(manifest['artifacts']):
                identities[name]=_copy_entry(source,entries[name],temp,ARTIFACTS[name],expected_hashes[name])
            _write(temp,'manifest.json',_json(manifest))
            _,manifest_hash=_verify_at(temp)
            for name,before in identities.items():
                raw,after=_read(source,entries[name]);_check(raw,expected_hashes[name])
                _require(before==after,'Source artifact identity changed during copy')
            with _directory(source_root) as current_source:
                _require(_identity(os.fstat(current_source))[:2]==_identity(os.fstat(source))[:2],
                         'Source root identity changed')
            os.fsync(temp)
            # Reopening validates the current path as well as the pinned directory before publication.
            with _directory(destination.parent) as current:
                _require(_identity(os.fstat(current))[:2]==_identity(os.fstat(parent))[:2],
                         'Archive parent identity changed')
            _publish_noreplace(parent,temporary,destination.name)
            published=True;os.fsync(parent)
            with _directory(destination.parent) as current_parent:
                _require(_identity(os.fstat(current_parent))[:2]==_identity(os.fstat(parent))[:2],
                         'Archive parent changed during publication')
            with _directory(destination) as current_bundle:
                _,verified_hash=_verify_at(current_bundle)
                _require(verified_hash==manifest_hash,'Published archive identity differs')
        finally:
            os.close(temp)
            if not published:
                child=os.open(temporary,DIRECTORY,dir_fd=parent)
                try:
                    for name in os.listdir(child): os.unlink(name,dir_fd=child)
                finally: os.close(child)
                os.rmdir(temporary,dir_fd=parent)
    return dict(archivalStatus='archived',measurementStatus=measurement_status,destination=str(destination),
                manifestSha256=manifest_hash)


def destination_for(commit,run):
    _require(type(commit) is str and re.fullmatch('[0-9a-f]{40}',commit),'Invalid exact probe commit')
    _require(type(run) is str and re.fullmatch('[A-Za-z0-9][A-Za-z0-9_-]{0,79}',run),'Invalid run identifier')
    return ARCHIVE_ROOT/commit/run


def recover(claim):
    _require(type(claim) is dict and type(claim['schemaVersion']) is int and claim['schemaVersion']==1,
             'Invalid archival claim')
    expected=_manifest(claim['entries'],claim['expectedHashes'],claim['measurementStatus'])
    destination=Path(claim['destination'])
    # Check the original source hashes even when an existing destination can be reused.
    with _directory(claim['sourceRoot']) as source:
        for name,relative in claim['entries'].items():
            if relative is not None:
                raw,_=_read(source,relative);_check(raw,claim['expectedHashes'][name])
    if destination.exists() or destination.is_symlink():
        with _directory(destination) as directory:
            actual,manifest_hash=_verify_at(directory)
        _require(actual==expected,'Existing archive has another capture identity')
        return dict(archivalStatus='already-archived',measurementStatus=claim['measurementStatus'],
                    destination=str(destination),manifestSha256=manifest_hash)
    return publish_bundle(source_root=Path(claim['sourceRoot']),destination=destination,entries=claim['entries'],
                          measurement_status=claim['measurementStatus'],expected_hashes=claim['expectedHashes'])


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--claim',type=Path,required=True)
    args=parser.parse_args()
    claim_path=args.claim.absolute()
    if claim_path.is_relative_to('/tmp'):
        claim_path=Path('/tmp').resolve()/claim_path.relative_to('/tmp')
    with _directory(claim_path.parent) as parent: raw,_=_read(parent,claim_path.name)
    claim=json.loads(raw,object_pairs_hook=_unique)
    _require(Path(claim['destination'])==destination_for(claim['probeCommit'],claim['runId']),
             'Recovery destination differs from approved archive root')
    receipt=recover(claim)
    receipt_path=claim_path.with_name(claim_path.stem+'.receipt-'+uuid.uuid4().hex+'.json')
    with receipt_path.open('xb') as stream: stream.write(_json(receipt))
    print(json.dumps(receipt))
    return 0


if __name__=='__main__': raise SystemExit(main())
