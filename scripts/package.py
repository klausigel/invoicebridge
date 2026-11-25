# SPDX-License-Identifier: AGPL-3.0-or-later
"""Create a new review package. No installs, deletion, source invoice edits or uploads."""
import argparse,hashlib,json,re,shutil,xml.etree.ElementTree as ET
from pathlib import Path
from zipfile import ZipFile,ZIP_DEFLATED

p=argparse.ArgumentParser();p.add_argument('--version',required=True);p.add_argument('--publish-dir',type=Path,required=True)
p.add_argument('--include-local-sample',type=Path,metavar='PDF',help='Include the approved sample only in a private test package')
a=p.parse_args()
if not re.fullmatch(r'[0-9A-Za-z.-]+',a.version):raise ValueError('Invalid version')
if a.include_local_sample and hashlib.sha256(a.include_local_sample.read_bytes()).hexdigest() != 'b5f46736efb3df675ba7f4e8bfb8b3ec2e73732cb71fa186ad4777f91f2f300c':
 raise ValueError('Sample does not match the approved invoice')
root=Path(__file__).resolve().parent.parent
stage=root/'artifacts'/('Kig.InvoiceBridge-'+a.version+'-win-x64'+('-private-sample' if a.include_local_sample else ''))
archive=stage.parent/(stage.name+'.zip')
if stage.exists() or archive.exists():raise FileExistsError('Package output already exists')
stage.mkdir(parents=True)
def copy(src,dst):
 src=Path(src);dst=Path(dst);dst.parent.mkdir(parents=True,exist_ok=True)
 if src.is_dir():shutil.copytree(src,dst)
 else:shutil.copy2(src,dst)
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
publish=a.publish_dir.resolve()
for f in publish.iterdir():
 if f.is_file() and (f.suffix == '.dll' or f.name in {'invoicebridge.exe','invoicebridge.deps.json','invoicebridge.runtimeconfig.json'}):
  copy(f,stage/f.name)
if not (stage/'invoicebridge.exe').exists():raise FileNotFoundError('Published apphost missing')
# MIT-labeled NuGet runtime, NOT the differently licensed runtime ZIP.
nugets=root/'.tools/runtime-nuget'
runtime=nugets/'microsoft.netcore.app.runtime.win-x64.nupkg'
host=nugets/'microsoft.netcore.app.host.win-x64.nupkg'
for pkg in [runtime,host]:
 with ZipFile(pkg) as z:
  nuspec=next(n for n in z.namelist() if n.endswith('.nuspec'))
  meta=ET.fromstring(z.read(nuspec));ns={'n':meta.tag.split('}')[0].strip('{')}
  assert meta.find('n:metadata/n:license',ns).text=='MIT', 'Unexpected redistribution license'
  for n in z.namelist():
   if n.lower().endswith(('.nuspec','license.txt','third-party-notices.txt')): 
    dest=stage/'licenses/dotnet'/pkg.stem/Path(n).name;dest.parent.mkdir(parents=True,exist_ok=True);dest.write_bytes(z.read(n))
with ZipFile(runtime) as z:
 for n in z.namelist():
  prefix=next((s for s in ['runtimes/win-x64/native/','runtimes/win-x64/lib/net10.0/'] if n.startswith(s)),None)
  if prefix and not n.endswith('/'):
   dest=stage/'runtime/shared/Microsoft.NETCore.App/10.0.14'/n[len(prefix):]
   dest.parent.mkdir(parents=True,exist_ok=True);dest.write_bytes(z.read(n))
copy(stage/'runtime/shared/Microsoft.NETCore.App/10.0.14/hostfxr.dll',stage/'runtime/host/fxr/10.0.14/hostfxr.dll')
tools=stage/'tools';tools.mkdir()
for name in ['bin','lib','Resource','iccprofiles','doc']:
 copy(root/'.tools/ghostscript-10.08.0'/name,tools/'ghostscript-10.08.0'/name)
copy(root/'.tools/Mustang-CLI-2.26.0.jar',tools/'Mustang-CLI-2.26.0.jar')
for name in ['bin','config','documents']:
 copy(root/'.tools/verapdf'/name,tools/'verapdf'/name)
jre=next((root/'.tools/temurin-jre21.0.13').iterdir())
copy(jre,tools/'java') # retain original legal/, NOTICE and release metadata
for name in ['LICENSE','THIRD-PARTY-NOTICES.md']:
 copy(root/name,stage/name)
shutil.copytree(root/'licenses',stage/'licenses',dirs_exist_ok=True)
copy(root/'DEPLOYMENT-README.md',stage/'START-HIER.md')
copy(root/'LINUX.md',stage/'LINUX.md')
copy(root/'invoicebridge.example.json',stage/'invoicebridge.example.json')
if a.include_local_sample:copy(a.include_local_sample,stage/'invoices/sample_112684.pdf')
source=stage/'source';source.mkdir()
for name in ['Program.cs','KigInvoice.cs','PaymentDetails.cs','RgbRaster.cs','BridgeConfiguration.cs','InvoiceSourceResolver.cs','ToolPaths.cs','Kig.InvoiceBridge.csproj','global.json','NuGet.config','invoicebridge.example.json','packages.lock.json','LICENSE','DEPLOYMENT-README.md','LINUX.md']:
 copy(root/name,source/name)
copy(root/'scripts',source/'scripts')
for name in ['pdfpig/0.1.15/pdfpig.0.1.15.nupkg','zugferd-csharp/18.0.1/zugferd-csharp.18.0.1.nupkg','zxing.net/0.16.11/zxing.net.0.16.11.nupkg']:
 copy(root/'.packages'/name,source/'packages'/Path(name).name)
copy(root/'.tools/ghostscript-10.08.0.tar.xz',stage/'sources/ghostscript-10.08.0.tar.xz')
copy(root/'.tools/temurin21-sources.tar.gz',stage/'sources/temurin21-sources.tar.gz')
for f in (root/'.tools/redistribution-sources').glob('*'):
 if f.is_file():copy(f,stage/'sources'/f.name)
manifest={
 'packageVersion':a.version,'platform':'Windows x64',
 'runtime':'.NET 10.0.14 assembled from MIT-labeled official NuGet runtime + published MIT apphost',
 'java':'Eclipse Temurin 21.0.13+9 JRE; GPLv2 with Classpath Exception; matching source included',
 'runtimeNugetSha256':sha(runtime),'hostNugetSha256':sha(host),
 'includesPrivateSample':bool(a.include_local_sample),
 'files':[{'path':str(f.relative_to(stage)).replace('\\','/'),'size':f.stat().st_size,'sha256':sha(f)} for f in sorted(stage.rglob('*')) if f.is_file()]
}
(stage/'package-manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
with ZipFile(archive,'x',compression=ZIP_DEFLATED,compresslevel=6) as z:
 for f in stage.rglob('*'):
  if f.is_file():z.write(f,f.relative_to(stage))
(archive.parent/(archive.name+'.sha256')).write_text(sha(archive)+'  '+archive.name+'\n',encoding='ascii')
print(json.dumps({'stage':str(stage),'zip':str(archive),'sha256':sha(archive),'sizeBytes':archive.stat().st_size},indent=2))
