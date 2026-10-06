from pathlib import Path
import hashlib,json,math,struct,sys,xml.etree.ElementTree as E
root=Path(sys.argv[1]); out=Path(sys.argv[2])
manifest=next(root.rglob('point-shadow-seam.json')); m=json.loads(manifest.read_text())
f32=lambda x:struct.unpack('<f',struct.pack('<f',x))[0]
bits=lambda x:struct.pack('<f',x).hex()
assert m['schema']=='khaozengine.point-shadow-seam-evidence' and m['schemaVersion']==1 and m['status']=='complete'
assert m['provenance']['sourceCommit']=='8632b57391f1a0cf6d67bf77430c578f8d4fba50'
assert m['provenance']['sourceTree']=='d9c786f2a5fdb743f8d5b08cc8e8ba95b9db8ce2' and m['provenance']['revisionAgreement']=='agree'
assert m['device']['backend']=='Direct3D11Native' and m['device']['softwareAdapter'] is True and m['device']['deviceLossReason'] is None
assert m['grid']['stations']==13 and m['grid']['probesPerStation']==61 and m['recordedProbes']==793
captures={}
for c in m['captures']:
 data=(manifest.parent/c['file']).read_bytes()
 assert c['width']==192 and c['height']==192 and len(data)==c['bytes']==147456
 assert hashlib.sha256(data).hexdigest()==c['sha256']
 captures[c['name']]=data
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
trx=next(root.rglob('seam.trx')); tr=E.parse(trx).getroot(); counts=tr.find('t:ResultSummary/t:Counters',ns).attrib
assert [int(counts[k]) for k in ('total','executed','passed','failed','notExecuted')]==[1,1,0,1,0]
rows=tr.findall('.//t:UnitTestResult',ns);assert len(rows)==1 and rows[0].get('outcome')=='Failed'
assert rows[0].get('testName')=='KhaozEngine.Tests.Gpu.PointShadowFilterGpuTests.TheSoftEdgeCrossesACubeFaceBoundaryWithoutAStep'
error=rows[0].find('t:Output/t:ErrorInfo/t:Message',ns).text
assert 'shadow' in error and '-0.0087' in error

def stats(series, single=False):
 r=f32 if single else lambda x:x
 n=len(series);mx=r((n-1)/2);my=0
 for v in series:my=r(my+v)
 my=r(my/n);sxx=0;sxy=0
 for i,v in enumerate(series):
  dx=r(i-mx);sxx=r(sxx+r(dx*dx));sxy=r(sxy+r(dx*r(v-my)))
 slope=r(sxy/sxx);res=[r(v-r(my+r(slope*r(i-mx)))) for i,v in enumerate(series)]
 bump=r(r(r(res[3]+res[4])+res[5])/3)
 return {'bump':bump,'worst_elsewhere':max(abs(v) for i,v in enumerate(res) if i not in (3,4,5)),'residuals':res,'reach':series}

def channel(data,x,y):
 assert 0<=x<192 and 0<=y<192
 return data[(y*192+x)*4]
def bilinear(data,sx,sy):
 # Screen coordinates locate pixel centres at n+0.5, matching raster coordinates.
 x=sx-.5;y=sy-.5;ix=math.floor(x);iy=math.floor(y);fx=x-ix;fy=y-iy
 a=channel(data,ix,iy)*(1-fx)+channel(data,ix+1,iy)*fx
 b=channel(data,ix,iy+1)*(1-fx)+channel(data,ix+1,iy+1)*fx
 return a*(1-fy)+b*fy

reach=[];unique=[];endpoint=[];alternatives={k:[] for k in ('float64_same_texels','trapezoid_same_texels','bilinear_pixel_centres','screen_x_minus_quarter','screen_x_plus_quarter','screen_y_minus_quarter','screen_y_plus_quarter')}
projection_max=0;projected_pixel_mismatches=0
matrix=[f32(v) for v in m['camera']['viewProjection']];origin=m['camera']['renderOrigin']
for si,s in enumerate(m['stations']):
 assert s['index']==si and len(s['probes'])==61
 total=0;profile=[];alt={k:[] for k in alternatives};coords=set()
 for pi,p in enumerate(s['probes']):
  assert p['index']==pi
  x,y=p['pixel'];sx,sy=[f32(v) for v in p['screen']]
  assert x==int(sx) and y==int(sy) and p['byteIndex']==(y*192+x)*4
  a=channel(captures['plain'],x,y);b=channel(captures['soft'],x,y)
  assert (a,b)==(p['plainRed'],p['shadowRed']) and a>24
  v=f32(b/a);assert bits(v)==bits(p['ratio']);profile.append(v);coords.add((x,y));total=f32(total+f32(1-v))
  wx,wy,wz=[f32(v)-origin[i] for i,v in enumerate(p['world'])]
  clip=[wx*matrix[j]+wy*matrix[4+j]+wz*matrix[8+j]+matrix[12+j] for j in range(4)]
  screen=((clip[0]/clip[3]+1)*96,(1-clip[1]/clip[3])*96)
  projection_max=max(projection_max,abs(screen[0]-sx),abs(screen[1]-sy))
  projected_pixel_mismatches+=int((int(screen[0]),int(screen[1]))!=(x,y))
  alt['bilinear_pixel_centres'].append(bilinear(captures['soft'],sx,sy)/bilinear(captures['plain'],sx,sy))
  for key,dx,dy in [('screen_x_minus_quarter',-.25,0),('screen_x_plus_quarter',.25,0),('screen_y_minus_quarter',0,-.25),('screen_y_plus_quarter',0,.25)]:
   ax,ay=int(sx+dx),int(sy+dy)
   alt[key].append(channel(captures['soft'],ax,ay)/channel(captures['plain'],ax,ay))
 assert bits(total)==bits(s['sum'])
 r=f32(f32(f32(total*2)*f32(.9))/60);assert bits(r)==bits(s['reach']);reach.append(r)
 unique.append(len(coords));endpoint.append([profile[0],profile[-1]])
 alternatives['float64_same_texels'].append(sum(1-v for v in profile)*1.8/60)
 alternatives['trapezoid_same_texels'].append((sum(1-v for v in profile)-.5*(1-profile[0])-.5*(1-profile[-1]))*1.8/60)
 for k in alt:
  if alt[k]:alternatives[k].append(sum(1-v for v in alt[k])*1.8/60)
original=stats(reach,True)
assert bits(original['bump'])==bits(m['statistic']['bump'])
assert bits(original['worst_elsewhere'])==bits(m['statistic']['worstElsewhere'])
assert all(bits(a)==bits(b) for a,b in zip(original['residuals'],m['fit']['residuals']))
result={'run_id':37482238342,'job_id':112333092389,'artifact_id':11421552678,'code_sha':'be72718673d79d83d34d3467a262e95bbe845151','proof_sha':m['provenance']['sourceCommit'],'proof_tree':m['provenance']['sourceTree'],'assertion':'Failed, unchanged 0.008 m bound','artifact':'Complete and independently verified','manifest_sha256':hashlib.sha256(manifest.read_bytes()).hexdigest(),'trx_sha256':hashlib.sha256(trx.read_bytes()).hexdigest(),'capture_sha256':{k:hashlib.sha256(v).hexdigest() for k,v in captures.items()},'original_float32_bitwise_replay':original,'unique_texels_per_station':unique,'profile_endpoints':endpoint,'double_reference_projection_max_pixel_error':projection_max,'double_reference_projection_pixel_mismatches':projected_pixel_mismatches,'offline_sensitivity_only':{k:stats(v) for k,v in alternatives.items()},'limits':['All alternatives use the same captured images. They are not new rendered proofs or replacement acceptance criteria.','No clamped-face negative control or shadow-atlas/tap capture exists in this artifact.','Rounded agreement with historical vectors is not historical pixel byte equality.']}
out.write_text(json.dumps(result,indent=2)+'\n')
print('All artifact gates and 793 byte/ratio pairs validated. Original f32 result replayed bit for bit.')
print('original',original['bump'],original['worst_elsewhere'])
print('unique texels',unique,'endpoints',endpoint)
print('projection',projection_max,projected_pixel_mismatches)
for k,v in result['offline_sensitivity_only'].items():print(k,v['bump'],v['worst_elsewhere'])
