import bpy,struct,pathlib,math
from mathutils import Vector
P=pathlib.Path(__file__).resolve().parent.parent/'Resources'/'Citizens'
bpy.ops.object.select_all(action='SELECT');bpy.ops.object.delete(use_global=False)
for o,outfit in enumerate(['male_casualsuit01','male_casualsuit02','male_casualsuit05','female_casualsuit01']):
 for name,tex in [('body_'+outfit,'skin2' if o==3 else 'skin0'),(outfit,outfit+'_diffuse'),('shoes01','shoes01_diffuse'),('bob02' if o==3 else 'short02',('bob02' if o==3 else 'short02')+'_diffuse'),('low-poly','eyes')]:
  with (P/(name+'.bytes')).open('rb') as f:
   nv,nt=struct.unpack('<ii',f.read(8));verts=[];uv=[]
   for i in range(nv):
    v=struct.unpack('<8f4i4f',f.read(64));verts.append((v[0]+o*.6,v[2],v[1]));uv.append(v[6:8])
   ids=struct.unpack('<'+str(nt)+'i',f.read(nt*4));faces=[(ids[i+2],ids[i+1],ids[i]) for i in range(0,nt,3)]
  mesh=bpy.data.meshes.new(name);mesh.from_pydata(verts,[],faces);mesh.update();obj=bpy.data.objects.new(name,mesh);bpy.context.collection.objects.link(obj)
  uvmap=mesh.uv_layers.new()
  for poly in mesh.polygons:
   poly.use_smooth=True
   for li in poly.loop_indices:uvmap.data[li].uv=uv[mesh.loops[li].vertex_index]
  mat=bpy.data.materials.new(name);mat.use_nodes=True;mat.use_backface_culling=True;bs=mat.node_tree.nodes.get('Principled BSDF');bs.inputs['Roughness'].default_value=.62
  if (P/(tex+'.png')).exists():
   tx=mat.node_tree.nodes.new('ShaderNodeTexImage');tx.image=bpy.data.images.load(str(P/(tex+'.png')));mat.node_tree.links.new(tx.outputs['Color'],bs.inputs['Base Color']);mat.node_tree.links.new(tx.outputs['Alpha'],bs.inputs['Alpha'])
  obj.data.materials.append(mat)
bpy.ops.mesh.primitive_plane_add(size=200);bpy.context.object.location.z=-.02
bpy.ops.object.light_add(type='AREA',location=(1,3,5));bpy.context.object.data.energy=600;bpy.context.object.data.shape='DISK';bpy.context.object.data.size=4
bpy.ops.object.camera_add(location=(3.4,5,2.4));cam=bpy.context.object;cam.rotation_euler=(Vector((.9,0,.85))-cam.location).to_track_quat('-Z','Y').to_euler();cam.data.type='ORTHO';cam.data.ortho_scale=3.2
s=bpy.context.scene;s.camera=cam;s.render.engine='CYCLES';s.cycles.samples=24;s.render.resolution_x=1600;s.render.resolution_y=1050;s.render.resolution_percentage=100;s.world.color=(.35,.35,.35);s.render.filepath=str(P.parent.parent/'Source~'/'citizens-preview.png');bpy.ops.render.render(write_still=True)
