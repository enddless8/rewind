"""Paired profiling of an exact retained first-save AV1 snapshot; outputs in artifacts."""
import ctypes, hashlib, json, statistics, subprocess, sys, time
from pathlib import Path

repo = Path(__file__).resolve().parents[2]
source = Path(sys.argv[1]).resolve()
root = Path(sys.argv[2]).resolve()
assert (repo/'artifacts') in source.parents and (repo/'artifacts') in root.parents
root.mkdir(parents=True, exist_ok=False)
ff = repo/'.tools/ffmpeg/ffmpeg.exe'
lab = repo/'.tools/decoder/ffmpeg.exe'
template = json.loads((source/'export-arguments.json').read_text(encoding='utf-8-sig'))
inputs = {str(p):hashlib.sha256(p.read_bytes()).hexdigest() for p in (source/'snapshot').iterdir() if p.is_file()}

def variant(name, output):
    args = template.copy(); args[-1] = str(output)
    if name == 'filter-1': args[:0] = ['-filter_complex_threads','1']
    if name == 'hwframes-32': args[args.index('-i'):args.index('-i')] = ['-extra_hw_frames','32']
    if name == 'combined':
        args[:0] = ['-filter_complex_threads','1']
        args[args.index('-i'):args.index('-i')] = ['-extra_hw_frames','32']
    return ['-progress','pipe:1','-stats_period','0.1',*args]

def run(args):
    begin = time.perf_counter(); first = None; count = 0
    child = subprocess.Popen([str(ff),*args], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, creationflags=subprocess.CREATE_NO_WINDOW)
    launch = (time.perf_counter()-begin)*1000
    # Progress is small; stderr at -v error is read after success. All commands bounded.
    import threading
    errors = []
    reader = threading.Thread(target=lambda: errors.append(child.stderr.read()), daemon=True); reader.start()
    def timeout():
        try: child.kill()
        except OSError: pass
    timer = threading.Timer(180, timeout); timer.start()
    try:
        for line in child.stdout:
            if line.startswith('frame='):
                count = int(line[6:])
                if count and first is None: first = (time.perf_counter()-begin)*1000
        child.wait(); reader.join()
        elapsed = (time.perf_counter()-begin)*1000
        if child.returncode or ''.join(errors).strip(): raise RuntimeError(''.join(errors))
        return dict(ms=elapsed, launch_ms=launch, first_progress_ms=first, frames=count)
    finally:
        timer.cancel()
        if child.poll() is None: child.kill(); child.wait()

rows = []
names = ['baseline','filter-1'] if len(sys.argv) > 3 and sys.argv[3] == 'filters' else ['baseline','filter-1','hwframes-32','combined']
for repetition in range(3):
    for name in names if repetition % 2 == 0 else names[::-1]:
        output = root/f'{name}-{repetition+1}.mkv'
        args = variant(name, output)
        row = dict(name=name, repetition=repetition+1, output=str(output), **run(args))
        rows.append(row)
        (root/'timings.json').write_text(json.dumps(rows,indent=2),encoding='utf-8')
        print(name, repetition+1, round(row['ms'],1), row['frames'], flush=True)

def integrity(path):
    probe = subprocess.run([str(repo/'.tools/ffmpeg/ffprobe.exe'),'-v','error','-show_packets','-show_data_hash','sha256',
        '-show_entries','packet=stream_index,pts,dts,duration,data_hash','-of','json',str(path)], capture_output=True, text=True, check=True, creationflags=subprocess.CREATE_NO_WINDOW)
    packets = json.loads(probe.stdout)['packets']
    decoded = subprocess.run([str(lab),'-v','error','-xerror','-c:v','libdav1d','-i',str(path),'-map','0',
        '-fps_mode:v','passthrough','-f','framemd5','-'], capture_output=True, text=True, check=True, creationflags=subprocess.CREATE_NO_WINDOW)
    if decoded.stderr.strip(): raise RuntimeError(decoded.stderr)
    frames = [x for x in decoded.stdout.splitlines() if x and not x.startswith('#')]
    return packets,frames

reference = integrity(Path(rows[0]['output']))
checks = []
for row in rows:
    actual = integrity(Path(row['output']))
    # Exact packet payload, stream index, timestamps, AAC and independently decoded frames.
    ok = actual == reference and row['frames'] == 1800
    checks.append(dict(name=row['name'], repetition=row['repetition'], identical_packets_frames_audio=ok))
    if not ok: raise AssertionError(checks[-1])
assert inputs == {str(p):hashlib.sha256(p.read_bytes()).hexdigest() for p in (source/'snapshot').iterdir() if p.is_file()}
summary = {name: dict(median_ms=statistics.median(r['ms'] for r in rows if r['name']==name),
    min_ms=min(r['ms'] for r in rows if r['name']==name), max_ms=max(r['ms'] for r in rows if r['name']==name)) for name in names}
(root/'results.json').write_text(json.dumps(dict(passed=True, summary=summary, checks=checks, input_hashes=inputs),indent=2),encoding='utf-8')
print(json.dumps(summary,indent=2),flush=True)
