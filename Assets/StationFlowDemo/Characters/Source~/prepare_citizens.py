"""Reproducible CC0 MakeHuman subset -> compact Unity skin packs. No add-on code used."""
import json, pathlib, struct, numpy as np
from PIL import Image
class cKDTree:
    def __init__(self,points):self.points=points
    def query(self,p):
        d=((self.points-p)**2).sum(axis=1);i=int(np.argmin(d));return float(d[i]**.5),i

ROOT=pathlib.Path(__file__).resolve().parent
OUT=ROOT.parent/'Resources'/'Citizens'; OUT.mkdir(exist_ok=True,parents=True)

def obj(path,body_only=False):
    vs=[];uv=[];faces=[];group=''
    for line in path.read_text().splitlines():
        p=line.split()
        if not p:continue
        if p[0]=='v':vs.append(list(map(float,p[1:4])))
        elif p[0]=='vt':uv.append(list(map(float,p[1:3])))
        elif p[0]=='g':group=p[1]
        elif p[0]=='f' and (not body_only or group=='body'):
            f=[(int(a.split('/')[0])-1,int(a.split('/')[1])-1 if '/' in a else 0) for a in p[1:]]
            for i in range(1,len(f)-1):faces.append([f[0],f[i],f[i+1]])
    return np.array(vs),np.array(uv),faces

base,baseuv,basefaces=obj(ROOT/'base.obj',True)
rig=json.loads((ROOT/'default.mhskel').read_text())
names=['root','spine03','spine01','neck01','head','upperarm01.L','lowerarm01.L','wrist.L','upperarm01.R','lowerarm01.R','wrist.R','upperleg01.L','lowerleg01.L','foot.L','upperleg01.R','lowerleg01.R','foot.R']
def collapse(n):
    while n not in names:n=rig['bones'][n]['parent']
    return names.index(n)
parents=[-1 if n=='root' else collapse(rig['bones'][n]['parent']) for n in names]
joints=np.array([np.mean(base[rig['joints'][rig['bones'][n]['head']]],axis=0) for n in names])

def weight_arrays(data,count):
    w=np.zeros((count,len(names)))
    for name,entries in data.items():
        if name not in rig['bones']:continue
        b=collapse(name)
        for i,value in entries:
            if i<count:w[i,b]+=value
    return w
weights=weight_arrays(json.loads((ROOT/'default_weights.mhw').read_text())['weights'],len(base))
valid=np.where(weights.sum(axis=1)>.01)[0];tree=cKDTree(base[valid])
for i in np.where(weights.sum(axis=1)<.01)[0]:weights[i]=weights[valid[tree.query(base[i])[1]]]
weights/=weights.sum(axis=1)[:,None]

def fromto(a,b):
    a=a/np.linalg.norm(a);b=b/np.linalg.norm(b);v=np.cross(a,b);c=np.dot(a,b)
    skew=np.array([[0,-v[2],v[1]],[v[2],0,-v[0]],[-v[1],v[0],0]])
    return np.eye(3)+skew+skew@skew/(1+c)

rot=[];posed=[]
for i,n in enumerate(names):
    parent=parents[i]
    pos=joints[i] if parent<0 else posed[parent]+rot[parent]@(joints[i]-joints[parent])
    r=np.eye(3) if parent<0 else rot[parent]
    if n.startswith(('upperarm','lowerarm','upperleg','lowerleg')):
        child=i+1;old=joints[child]-joints[i]
        side=1 if n.endswith('.L') else -1
        target=np.array([side*(.16 if n.startswith('upperarm') else .015 if n.startswith('lowerarm') else 0),-1,.045 if 'arm' in n else .015])
        r=fromto(old,target)
    rot.append(r);posed.append(pos)
rot=np.array(rot);posed=np.array(posed)

def bake(vs,w):
    result=np.zeros_like(vs)
    for i in range(len(names)):result+=((vs-joints[i])@rot[i].T+posed[i])*w[:,i,None]
    result[:,1]+=8.4488
    return result*.1

def mhclo(path,vs):
    lines=path.read_text().splitlines();mode=None;rows=[];delete=[];scale=np.ones(3)
    for line in lines:
        p=line.split()
        if not p or p[0]=='#':continue
        if p[0] in ['x_scale','y_scale','z_scale']:
            axis=['x_scale','y_scale','z_scale'].index(p[0]);scale[axis]=abs(base[int(p[1]),axis]-base[int(p[2]),axis])/float(p[3])
        if p[0]=='verts':mode='verts';continue
        if p[0]=='delete_verts':mode='delete';continue
        if not p[0].lstrip('-').replace('.','').isdigit():mode=None;continue
        if mode=='verts':rows.append(p)
        elif mode=='delete':delete+=p
    hidden=set();i=0
    while i<len(delete):
        start=int(delete[i]);i+=1
        if i<len(delete) and delete[i]=='-':hidden.update(range(start,int(delete[i+1])+1));i+=2
        else:hidden.add(start)
    result=np.zeros((len(vs),len(names)));fitted=np.array(vs)
    for i,p in enumerate(rows[:len(vs)]):
        if len(p)==1:result[i]=weights[int(p[0])];fitted[i]=base[int(p[0])]
        else:
            for j in range(3):result[i]+=weights[int(p[j])]*float(p[j+3])
            fitted[i]=sum(base[int(p[j])]*float(p[j+3]) for j in range(3))+np.array(list(map(float,p[6:9])))*scale
    result=np.maximum(result,0)
    for i in np.where(result.sum(axis=1)<.01)[0]:result[i]=weights[valid[tree.query(vs[i])[1]]]
    result/=result.sum(axis=1)[:,None]
    return result,hidden,fitted

def write_pack(name,vs,uv,faces,w,hidden=set()):
    vs=bake(vs,w);normal=np.zeros_like(vs);good=[]
    for f in faces:
        ids=[c[0] for c in f]
        if any(i in hidden for i in ids):continue
        n=np.cross(vs[ids[1]]-vs[ids[0]],vs[ids[2]]-vs[ids[0]])
        for i in ids:normal[i]+=n
        good.append(f)
    normal/=np.maximum(.0000001,np.linalg.norm(normal,axis=1))[:,None]
    unique={};vertices=[];triangles=[]
    for f in good:
        # Numeric coordinates are preserved for Unity; keep triangles consistent with their outward cross-product normals.
        for k in f:
            if k not in unique:
                unique[k]=len(vertices);vertices.append(k)
            triangles.append(unique[k])
    with (OUT/(name+'.bytes')).open('wb') as f:
        f.write(struct.pack('<ii',len(vertices),len(triangles)))
        for vi,ui in vertices:
            inds=np.argsort(w[vi])[-4:][::-1];bw=w[vi,inds];bw/=bw.sum()
            f.write(struct.pack('<8f4i4f',*vs[vi],*normal[vi],*uv[ui],*inds,*bw))
        f.write(struct.pack('<'+str(len(triangles))+'i',*triangles))
    print(name,len(vertices),'vertices',len(triangles)//3,'triangles')

def texture(path,name,size=1024):
    if path.exists():
        im=Image.open(path);im.thumbnail((size,size));im.save(OUT/(name+'.png'))

def asset(category,name):
    p=ROOT/'System'/category/name
    vs,uv,faces=obj(p/(name+'.obj'));w,hidden,vs=mhclo(p/(name+'.mhclo'),vs)
    own=p/(name+'.mhw')
    if own.exists():
        candidate=weight_arrays(json.loads(own.read_text())['weights'],len(vs));mask=candidate.sum(axis=1)>.01
        w[mask]=candidate[mask]/candidate[mask].sum(axis=1)[:,None]
    write_pack(name,vs,uv,faces,w)
    for role in ['diffuse','normal','ao']:texture(p/(name+'_'+role+'.png'),name+'_'+role)
    return hidden

outfits=['male_casualsuit01','male_casualsuit02','male_casualsuit05','female_casualsuit01']
shoehidden=asset('clothes','shoes01')
for outfit in outfits:
    hidden=asset('clothes',outfit)|shoehidden
    write_pack('body_'+outfit,base,baseuv,basefaces,weights,hidden)
for hair in ['short02','bob02']:asset('hair',hair)
asset('eyes','low-poly')
texture(ROOT/'System'/'eyes'/'materials'/'brown_eye.png','eyes',512)
for src,name in [('young_caucasian_male/young_lightskinned_male_diffuse.png','skin0'),('young_african_male/young_african_male_diffuse.png','skin1'),('young_caucasian_female/young_lightskinned_female_diffuse.png','skin2'),('middleage_asian_male/middleage_asian_male_diffuse.png','skin3')]:
    p=ROOT/'System'/'skins'/src
    if not p.exists():
        available=list(p.parent.glob('*.png'));p=available[0] if available else p
    texture(p,name)
with (OUT/'skeleton.json').open('w') as f:
    json.dump({'bones':[{'name':n,'parent':parents[i],'position':{'x':float(posed[i,0]*.1),'y':float((posed[i,1]+8.4488)*.1),'z':float(posed[i,2]*.1)}} for i,n in enumerate(names)]},f,separators=(',',':'))
print('Skeleton',[(n,[round(x*.1,3) for x in posed[i]]) for i,n in enumerate(names)])
