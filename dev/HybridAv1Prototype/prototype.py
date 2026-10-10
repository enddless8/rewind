"""Isolated AV1 research exporter. Never called by Rewind/SaveClip.

Inputs are recorder-owned, retained closed segments and already selected PCM WAVs.
Unknown AV1/Matroska syntax is rejected by the unchanged Phase 2 managed index.
No bitstream/header rewriting, arbitrary copy seeking, or production publication.
"""
from __future__ import annotations
import argparse, ctypes, dataclasses, hashlib, json, math, os, re, subprocess, threading, time
from pathlib import Path
from typing import Callable

REPO = Path(__file__).resolve().parents[2]
FF = REPO / '.tools/ffmpeg/ffmpeg-9.0.2-essentials_build/bin/ffmpeg.exe'
PROBE = FF.with_name('ffprobe.exe')
DOTNET = REPO / '.tools/dotnet/dotnet.exe'
INDEX = Path(__file__).parent / 'bin/Release/net10.0-windows/FidelityChecks.dll'
LAB = REPO / 'artifacts/save-profile/decoder-lab/ffmpeg-9.0.2-full_build/bin/ffmpeg.exe'
ROOT = REPO / 'artifacts/hybrid-phase3a'
BASE = ['-hide_banner', '-v', 'error', '-xerror']
GPU = ['-hwaccel', 'cuda', '-hwaccel_output_format', 'cuda', '-c:v', 'av1_cuvid']

def artifact_directory(path):
    path=Path(path).resolve()
    if path != ROOT.resolve() and ROOT.resolve() not in path.parents:
        raise ValueError('Prototype output разрешён только внутри ignored artifacts/hybrid-phase3a')
    path.mkdir(parents=True,exist_ok=True); return path

def dump(path, value):
    path = Path(path); path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_suffix(path.suffix + '.tmp')
    temp.write_text(json.dumps(value, indent=2, ensure_ascii=False), encoding='utf-8'); temp.replace(path)

def execute(binary, args, timeout=180, allow_failure=False):
    started = time.perf_counter()
    proc = subprocess.Popen([str(binary), *map(str, args)], stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            creationflags=subprocess.CREATE_NO_WINDOW)
    try: out, err = proc.communicate(timeout=timeout)
    except BaseException:
        proc.kill(); proc.communicate(); raise
    cpu = None; peak_memory = None
    if os.name == 'nt':
        times = [ctypes.c_ulonglong() for _ in range(4)]
        if ctypes.windll.kernel32.GetProcessTimes(ctypes.c_void_p(int(proc._handle)), *map(ctypes.byref, times)):
            cpu = (times[2].value + times[3].value) / 10000
        class Counters(ctypes.Structure):
            _fields_=[('cb',ctypes.c_uint32),('faults',ctypes.c_uint32)]+[(name,ctypes.c_size_t) for name in
                      ['peak_working_set','working_set','peak_paged','paged','peak_nonpaged','nonpaged','pagefile','peak_pagefile']]
        counters=Counters(); counters.cb=ctypes.sizeof(counters)
        if ctypes.windll.psapi.GetProcessMemoryInfo(ctypes.c_void_p(int(proc._handle)),ctypes.byref(counters),counters.cb):
            peak_memory=counters.peak_working_set
    record = dict(ms=(time.perf_counter()-started)*1000, cpu_ms=cpu, peak_working_set_bytes=peak_memory, code=proc.returncode,
                  command=[str(binary), *map(str,args)], stderr=err.decode('utf-8', errors='replace'))
    if not allow_failure and (proc.returncode or record['stderr'].strip()):
        raise RuntimeError(json.dumps(record, ensure_ascii=False))
    return out.decode('utf-8', errors='replace'), record

INDEX_CACHE = {}
SEQUENCE_CACHE = {}
def index(path):
    key=identity(path)
    if key not in INDEX_CACHE:
        text, _ = execute(DOTNET, [INDEX, 'index', path]); INDEX_CACHE[key]=json.loads(text)
        if len(INDEX_CACHE)>512:
            current=INDEX_CACHE[key];INDEX_CACHE.clear();INDEX_CACHE[key]=current
    return INDEX_CACHE[key]

def preindex(paths):
    paths=[str(Path(p).resolve()) for p in paths if identity(p) not in INDEX_CACHE]
    if paths:
        text,_=execute(DOTNET,[INDEX,'batch',*paths])
        if len(INDEX_CACHE)+len(paths)>512:INDEX_CACHE.clear()
        for path,value in json.loads(text).items(): INDEX_CACHE[identity(path)]=value

def compatible(a,b):
    return all(a[k] == b[k] for k in ['TimeScale','DefaultDuration','CodecPrivate','VideoProperties'])

def sequence_proof(path,format):
    key=format['CodecPrivate']
    if key not in SEQUENCE_CACHE:
        _, record=execute(FF,['-hide_banner','-v','verbose','-i',path,'-map','0:v:0','-c:v','copy','-frames:v','1',
                             '-bsf:v','trace_headers','-f','null','-'],allow_failure=True)
        if record['code']: raise ValueError('Не удалось разобрать sequence header')
        fields={m[0]:int(m[1]) for m in re.findall(r'\s([a-z0-9_]+(?:\[\d+\])?)\s+[01]+\s+=\s+(\d+)',record['stderr'])}
        if any(fields.get(k) != 0 for k in ['seq_profile','reduced_still_picture_header','timing_info_present_flag','operating_points_cnt_minus_1','frame_id_numbers_present_flag']):
            raise ValueError('Неизвестные decoder-model/operating-point/frame-ID ограничения')
        # With timing_info absent, decoder_model_info is absent by AV1 syntax.
        if len(SEQUENCE_CACHE)>=32:SEQUENCE_CACHE.clear()
        SEQUENCE_CACHE[key]=fields
        dump(ROOT/'sequence-proofs'/f'{hashlib.sha256(bytes.fromhex(key)).hexdigest()}.json',dict(codec_private=key,fields=fields,trace=record['stderr']))
    return SEQUENCE_CACHE[key]

def probe(path):
    text, _ = execute(PROBE, ['-v','error','-show_streams','-show_format','-of','json',path]); return json.loads(text)

@dataclasses.dataclass(frozen=True)
class Interval:
    path: str
    source_start: float
    source_end: float
    start: float
    end: float
    epoch: float | None = None

@dataclasses.dataclass(frozen=True)
class Snapshot:
    accepted: float
    history_start: float
    previous_cutoff: float
    epoch: float
    fps: int
    intervals: tuple[Interval, ...]
    audio: tuple[str, ...]
    mix_inputs: tuple[int,...] | None = None
    def __post_init__(self):
        object.__setattr__(self,'intervals',tuple(self.intervals));object.__setattr__(self,'audio',tuple(self.audio))
        if self.mix_inputs is not None:object.__setattr__(self,'mix_inputs',tuple(self.mix_inputs))
    @property
    def duration(self): return sum(r.end-r.start for r in self.intervals)

@dataclasses.dataclass(frozen=True)
class Piece:
    path: str
    first: int
    stop: int
    boundary_stop: int
    packet_hashes: tuple[str,...]

@dataclasses.dataclass(frozen=True)
class Plan:
    snapshot: Snapshot
    pieces: tuple[Piece,...]
    codec_private: str
    source_identities: tuple[tuple,...]
    audio_identities: tuple[tuple,...]

def identity(path):
    s = os.stat(path); return (s.st_dev, s.st_ino, s.st_size, s.st_mtime_ns)

def make_plan(snapshot):
    if not snapshot.intervals or snapshot.fps <= 0 or snapshot.duration <= 0:
        raise ValueError('Пустой/некорректный снимок')
    if not all(math.isfinite(v) for v in [snapshot.accepted,snapshot.history_start,snapshot.duration]) or math.isnan(snapshot.epoch) or snapshot.epoch==math.inf:
        raise ValueError('Неопределённая временная шкала')
    if math.isnan(snapshot.previous_cutoff) or snapshot.previous_cutoff == math.inf:
        raise ValueError('Некорректный previous cutoff')
    if snapshot.mix_inputs is not None and (len(set(snapshot.mix_inputs))!=len(snapshot.mix_inputs) or any(i<0 or i>=len(snapshot.audio) for i in snapshot.mix_inputs)):
        raise ValueError('Некорректный immutable audio routing')
    pieces=[]; first_index=None; ids=[]; previous_end=-math.inf
    for r in snapshot.intervals:
        if r.epoch is not None and r.epoch != snapshot.epoch:
            raise ValueError('Источник из другой rewind epoch')
        if not all(math.isfinite(v) for v in [r.source_start,r.source_end,r.start,r.end]) or not (
                r.source_start <= r.start < r.end <= r.source_end and r.end <= snapshot.accepted
                and r.start >= max(snapshot.history_start,snapshot.previous_cutoff,previous_end)):
            raise ValueError('Нарушена immutable граница истории/acceptance')
        ids.append(identity(r.path)); src=index(r.path)
        if ids[-1] != identity(r.path): raise ValueError('Источник изменился')
        if first_index is None: first_index=src
        if not compatible(first_index,src): raise ValueError('Несовместимые sequence header/av1C/metadata')
        sequence_proof(r.path,src)
        packets=src['Packets']; n=len(packets)
        if abs(src['DefaultDuration']-1e9/snapshot.fps)>1 or abs(n/snapshot.fps-(r.source_end-r.source_start))>1e-6:
            raise ValueError('Неподтверждённая CFR сетка')
        if any(p['Pts'] != round(i*1000/snapshot.fps) for i,p in enumerate(packets)):
            raise ValueError('Некорректный PTS/CFR порядок')
        # Same F6 seconds and native millisecond PTS as the decoded-trim filter.
        begin=float(f'{r.start-r.source_start:.6f}'); end=float(f'{r.end-r.source_start:.6f}')
        selected=[i for i,p in enumerate(packets) if begin <= p['Pts']/1000 < end]
        if not selected: raise ValueError('Нет кадров в интервале')
        first,stop=selected[0],selected[-1]+1
        if selected != list(range(first,stop)): raise ValueError('Разрыв кадров')
        boundary=first if packets[first]['RandomAccess'] else next(
            (i for i in range(first+1,stop) if packets[i]['RandomAccess']),stop)
        pieces.append(Piece(r.path,first,stop,boundary,tuple(p['Hash'] for p in packets[first:stop])))
        previous_end=r.end
    plan=Plan(snapshot,tuple(pieces),first_index['CodecPrivate'],tuple(ids),tuple(identity(path) for path in snapshot.audio))
    # Don't paper over a possible legacy CFR duplication with a different selection.
    expected=math.ceil(float(f'{snapshot.duration:.6f}')*snapshot.fps-1e-9)
    if sum(p.stop-p.first for p in pieces) != expected:
        raise ValueError('Неоднозначное округление сетки: нужен decoded reference/fallback')
    return plan

def manifest(path, pieces, fps):
    path.write_text(''.join(f"file '{str(p).replace(chr(92),'/').replace(chr(39),chr(39)+chr(92)+chr(39)+chr(39))}'\nduration {count/fps:.6f}\n"
                            for p,count in pieces),encoding='utf-8')

def audio_args(snapshot):
    args=[]
    for path in snapshot.audio: args += ['-i',path]
    if snapshot.audio:
        selected=tuple(range(len(snapshot.audio))) if snapshot.mix_inputs is None else snapshot.mix_inputs
        if selected:
            args += ['-filter_complex',''.join(f'[{i+1}:a]' for i in selected)+
                     f'amix=inputs={len(selected)}:normalize=0,alimiter=limit=0.95:latency=1[mix]', '-map','0:v:0','-map','[mix]']
        else: args+=['-map','0:v:0']
        for i,path in enumerate(snapshot.audio): args += ['-map',f'{i+1}:a:0']
        args += ['-c:a','aac','-b:a','160k']
        if selected:args+=['-metadata:s:a:0','title=Mix','-disposition:a:0','default']
        for i,path in enumerate(snapshot.audio):
            channel=i+int(bool(selected))
            args += [f'-metadata:s:a:{channel}',f'title={Path(path).stem}',f'-disposition:a:{channel}','0']
    else: args += ['-map','0:v:0','-an']
    return args

def export(plan, directory, strategy='hybrid'):
    if strategy not in ['hybrid','copy']: raise ValueError('Используйте отдельный full-NVDEC reference')
    directory=artifact_directory(directory)
    if tuple(identity(p.path) for p in plan.pieces) != plan.source_identities: raise ValueError('Изменён owned snapshot')
    if tuple(identity(path) for path in plan.snapshot.audio) != plan.audio_identities:raise ValueError('Изменён PCM snapshot')
    started=time.perf_counter(); stages=[]; snapshot=plan.snapshot; assembled=[]; hashes=[]; copied=[]; transitions=[]
    source_metadata=probe(plan.pieces[0].path)['streams'][0]
    sample_format=source_metadata['pix_fmt']
    raw_format='p010le' if sample_format=='yuv420p10le' else 'nv12'
    for number,p in enumerate(plan.pieces):
        src=index(p.path)
        head=p.boundary_stop-p.first
        if strategy == 'copy' and head: raise ValueError('Невыровненное начало не допускает Phase 2 copy')
        if head:
            raw=directory/f'boundary-{number}.nut'; boundary=directory/f'boundary-{number}.mkv'
            # Explicitly split decode/encode to measure each. Extra temp/startup cost is intentional research overhead.
            storage_format='yuv420p10le' if raw_format=='p010le' else raw_format
            _, timing=execute(FF,[*BASE,*GPU,'-i',p.path,'-vf',f'trim=start_frame={p.first}:end_frame={p.first+head},setpts=PTS-STARTPTS,hwdownload,format={raw_format},format={storage_format}',
                                  '-an','-c:v','rawvideo','-fps_mode','passthrough','-f','nut','-y',raw])
            stages.append(dict(stage='boundary-decode+raw-intermediate',**timing))
            if probe(raw)['streams'][0]['pix_fmt'] != storage_format:
                raise ValueError('Raw intermediate pixel format не сохранён')
            params=[]
            for field,option in [('color_range','range'),('color_space','colorspace'),('color_transfer','color_trc'),('color_primaries','color_primaries')]:
                if field in source_metadata: params.append(option+'='+source_metadata[field])
            vf=('setparams='+':'.join(params)+',' if params else '')+'format='+raw_format
            _, timing=execute(FF,[*BASE,'-i',raw,'-vf',vf,'-an','-c:v','av1_nvenc','-preset','p4','-tune','hq','-rc','vbr','-cq','27',
                                  '-b:v','0','-maxrate','15M','-bufsize','30M','-bf','0','-g',str(snapshot.fps*2),'-r',str(snapshot.fps),'-y',boundary])
            stages.append(dict(stage='boundary-encode',**timing)); encoded=index(boundary)
            if not compatible(src,encoded) or len(encoded['Packets'])!=head: raise ValueError('Boundary av1C/metadata/frame count несовместимы')
            hashes.extend(x['Hash'] for x in encoded['Packets']); copied.extend([False]*head)
            assembled.append((boundary,head)); transitions.append(dict(output_frame=len(hashes),source=p.path,source_frame=p.first+head))
            # Preserve metrics, release only this invocation's raw intermediate to bound disk usage.
            stages[-1]['raw_bytes']=raw.stat().st_size; raw.unlink()
        remaining=p.stop-p.first-head
        if remaining:
            if p.first+head != 0:
                # Current recorder starts each segment at KEY; middle-GOP packet extraction needs a separate verified muxer.
                raise ValueError('KEY внутри сегмента требует отдельной доказанной packet extraction')
            if p.stop != len(src['Packets']):
                tail=directory/f'tail-{number}.mkv'
                _, timing=execute(FF,[*BASE,'-i',p.path,'-map','0:v:0','-an','-c:v','copy','-frames:v',str(p.stop),'-y',tail])
                stages.append(dict(stage='tail-prefix-copy',**timing)); got=index(tail)
                if not compatible(src,got) or [x['Hash'] for x in got['Packets']]!=list(p.packet_hashes):
                    raise ValueError('Tail payload не совпадает')
                assembled.append((tail,remaining))
            else: assembled.append((Path(p.path),remaining))
            hashes.extend(p.packet_hashes); copied.extend([True]*remaining)
    video=directory/'hybrid.txt'; manifest(video,assembled,snapshot.fps)
    output=directory/'unpublished.mkv'
    # A proved one-packet/one-shown-frame CFR stream permits a new global grid.
    # Avoid double millisecond quantization at fractional-frame concat offsets.
    ticks=f'round(N/({snapshot.fps}*TB))'
    _,timing=execute(FF,[*BASE,'-f','concat','-safe','0','-i',video,*audio_args(snapshot),'-c:v','copy',
                        '-bsf:v',f'setts=pts={ticks}:dts={ticks}','-t',f'{snapshot.duration:.6f}','-y',output])
    stages.append(dict(stage='interior-copy+AAC+mux',**timing))
    produced=index(output); fps=snapshot.fps
    if len(produced['Packets'])!=len(hashes) or [x['Hash'] for x in produced['Packets']]!=hashes:
        raise ValueError('Потеря/дубликат/изменение video payload')
    if any(p['Pts'] != round(i*1000/fps) for i,p in enumerate(produced['Packets'])):
        raise ValueError('Неправильный timestamp перехода')
    decoded,timing=execute(FF,[*BASE,*GPU,'-i',output,'-map','0','-fps_mode:v','passthrough','-progress','pipe:1','-f','null','-'])
    counts=re.findall(r'^frame=(\d+)',decoded,re.MULTILINE)
    if not counts or int(counts[-1]) != len(hashes) or 'progress=end' not in decoded:
        raise ValueError('Полное декодирование не подтвердило все selected frames')
    stages.append(dict(stage='full-video+audio-validation',**timing))
    description=probe(output)
    mix=bool(snapshot.audio) and (snapshot.mix_inputs is None or bool(snapshot.mix_inputs))
    if len(description['streams']) != 1+len(snapshot.audio)+int(mix): raise ValueError('Потеря аудиодорожек')
    if tuple(identity(piece.path) for piece in plan.pieces) != plan.source_identities:
        raise ValueError('Owned источник изменился во время export')
    if tuple(identity(path) for path in snapshot.audio) != plan.audio_identities:raise ValueError('PCM источник изменился во время export')
    result=dict(strategy=strategy,total_ms=(time.perf_counter()-started)*1000,stages=stages,frames=len(hashes),
                copied_frames=sum(copied),boundary_frames=len(hashes)-sum(copied),transitions=transitions,
                output=str(output),bytes=output.stat().st_size,temporary_bytes=sum(Path(p).stat().st_size for p,n in assembled if Path(p).parent==directory),
                max_raw_bytes=max((s.get('raw_bytes',0) for s in stages),default=0),snapshot=dataclasses.asdict(snapshot))
    dump(directory/'export.json',result); return result

def reference(snapshot,directory):
    """Unchanged production-style full concat / trim / CUDA decode / NVENC / AAC."""
    directory=artifact_directory(directory)
    video=directory/'source.txt'; manifest(video,[(Path(r.path),round((r.source_end-r.source_start)*snapshot.fps)) for r in snapshot.intervals],snapshot.fps)
    audio=audio_args(snapshot); at=audio.index('-filter_complex') if '-filter_complex' in audio else -1
    trim=f'[0:v]trim=start={snapshot.intervals[0].start-snapshot.intervals[0].source_start:.6f}:duration={snapshot.duration:.6f},setpts=PTS-STARTPTS[v]'
    if at>=0: audio[at+1]=trim+';'+audio[at+1]; audio[audio.index('0:v:0')]='[v]'
    else:
        audio[0:0]=['-filter_complex',trim];audio[audio.index('0:v:0')]='[v]'
    out=directory/'reference.mkv'; started=time.perf_counter()
    _,timing=execute(FF,[*BASE,*GPU,'-f','concat','-safe','0','-i',video,*audio,'-c:v','av1_nvenc','-preset','p4','-tune','hq','-rc','vbr','-cq','27',
                        '-b:v','0','-maxrate','15M','-bufsize','30M','-bf','0','-r',str(snapshot.fps),'-t',f'{snapshot.duration:.6f}','-y',out])
    _,validation=execute(FF,[*BASE,*GPU,'-i',out,'-map','0','-fps_mode:v','passthrough','-f','null','-'])
    result=dict(strategy='full-nvdec',output=str(out),total_ms=(time.perf_counter()-started)*1000,stages=[dict(stage='full-NVDEC+NVENC+AAC+mux',**timing),dict(stage='full-validation',**validation)])
    dump(directory/'export.json',result); return result

def retained_snapshot(seconds,start=0.0,end=None):
    root=REPO/f'artifacts/gop-phase2/benchmark/snapshot-{seconds}-A-copy-1'
    args=json.loads((root/'copy-args.json').read_text(encoding='utf-8-sig')); duration=float(args[args.index('-t')+1])
    end=duration if end is None else end
    lines=(root/'video.txt').read_text(encoding='utf-8').splitlines(); intervals=[]; clock=0
    for i in range(0,len(lines),2):
        path=lines[i][6:-1]; length=float(lines[i+1].split()[1])
        if clock<end and clock+length>start: intervals.append(Interval(path,clock,clock+length,max(start,clock),min(end,clock+length)))
        clock+=length
    audio=tuple(str(root/Path(args[i+1]).name) for i,a in enumerate(args[:-1]) if a=='-i' and args[i+1].lower().endswith('.wav'))
    mix=tuple(int(i)-1 for i in re.findall(r'\[(\d+):a\]',args[args.index('-filter_complex')+1])) if '-filter_complex' in args else ()
    return Snapshot(end,start,-math.inf,0,60,tuple(intervals),audio,mix)

def crop_audio(snapshot, directory, start):
    if not snapshot.audio or start==0: return snapshot
    directory=artifact_directory(directory); files=[]
    for path in snapshot.audio:
        target=directory/Path(path).name
        if target.resolve()==Path(path).resolve(): raise ValueError('Нельзя перезаписать original PCM snapshot')
        execute(FF,[*BASE,'-i',path,'-af',f'atrim=start={start:.6f}:duration={snapshot.duration:.6f},asetpts=PTS-STARTPTS','-c:a','pcm_s16le','-y',target])
        files.append(str(target))
    return dataclasses.replace(snapshot,audio=tuple(files))

def main():
    parser=argparse.ArgumentParser(); parser.add_argument('--seconds',type=int,default=15); parser.add_argument('--start',type=float,default=0.75)
    parser.add_argument('--strategy',choices=['hybrid','copy'],default='hybrid'); parser.add_argument('--out',default='trial'); args=parser.parse_args()
    directory=ROOT/args.out; snapshot=crop_audio(retained_snapshot(args.seconds,args.start),directory/'pcm',args.start)
    preindex([r.path for r in snapshot.intervals]); result=export(make_plan(snapshot),directory,args.strategy)
    print(json.dumps({k:v for k,v in result.items() if k not in ['stages','snapshot']},indent=2))

if __name__=='__main__': main()
