import json,os,sys
import checks as c

def main():
    for mode in ('locked','no-space'):
        folder=c.fixture('extra/'+mode)
        result=c.finalize(folder,mode)
        c.check(result.get('retained') and not list((folder/'output').glob('*.mkv')),'Publication prevented: '+mode)
        c.check((folder/'video-000000000.mkv').exists() and (folder/'Microphone.pcm').exists(),'All owned video/PCM retained: '+mode)
    for codec in ('h264_nvenc','hevc_nvenc'):
        folder=c.fixture('extra/'+codec,tracks=())
        for file in folder.glob('video-*.mkv'):
            source=file.read_bytes();file.unlink()
            c.call(c.FF,['-v','error','-i',c.SOURCE/'part-000.mkv','-an','-c:v',codec,'-bf','0','-r','30','-y',file])
        manifest=json.loads((folder/'recording.json').read_text(encoding='utf-8'));manifest['Encoder']=codec;c.dump(folder/'recording.json',manifest)
        result=c.finalize(folder);meta=json.loads(c.Path(result['clip']['Path']).with_suffix('.json').read_text(encoding='utf-8'))
        c.check(meta['Export']['Strategy']=='legacy-reencode','Original codec finalizer preserved: '+codec)
        c.check(len(c.frames(result['clip']['Path']))==180,'Fallback fully decodes 180 frames: '+codec)
    for name,source in [('header','changed-private.mkv'),('preroll','unsupported-preroll.mkv')]:
        folder=c.fixture('extra/'+name)
        target=folder/'video-000000001.mkv';target.unlink();os.link(c.REPO/'artifacts/gop-phase2/final-release-v4/media'/source,target)
        text,*_=c.call(c.DOTNET,[c.HARNESS,'plan',folder]);c.check(not json.loads(text)['eligible'],'Incompatible metadata rejected: '+name)
    folder=c.fixture('extra/corrupt-input');target=folder/'video-000000000.mkv';data=target.read_bytes();target.unlink();target.write_bytes(data[:100])
    text,err,code,*_=c.call(c.DOTNET,[c.HARNESS,'finalize',folder],allowed=True)
    c.check(code!=0 and not list((folder/'output').glob('*.mkv')),'Corrupt source cannot publish through legacy fallback')
    c.check(target.exists() and (folder/'System.pcm').exists(),'Corrupt source/recovery material preserved')
    recovery()
def recovery():
    folder=c.fixture('extra/recovery-fixed',start=1,end=5)
    manifest=json.loads((folder/'recording.json').read_text(encoding='utf-8'))
    manifest['Settings']['OutputDirectory']=str(c.ROOT/'extra/recovery-output')
    c.dump(folder/'recording.json',manifest)
    # Actual existing unfinished-session recovery through the new production finalizer.
    output,*_=c.call(c.DOTNET,[c.HARNESS,'recover',folder]);path=c.Path(output.strip())
    c.check(path.exists() and len(c.frames(path))==120,'Recovery exact interval fully decodes')
    c.check(not folder.exists(),'Recovery releases ownership only after publication')
    c.dump(c.ROOT/'extra-results.json',dict(passed=True,count=len(c.CHECKS),checks=c.CHECKS))
if __name__=='__main__':
    if len(sys.argv)>1 and sys.argv[1]=='recovery':
        c.CHECKS.extend(x for x in json.loads((c.ROOT/'checks-progress.json').read_text(encoding='utf-8')) if x['ok'])
        recovery()
    else:main()
