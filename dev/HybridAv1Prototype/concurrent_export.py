import dataclasses,json,math,sys
import prototype as p

def main():
    data=json.loads(p.Path(sys.argv[1]).read_text(encoding='utf-8'));data['epoch']=float(data['epoch'])
    data['previous_cutoff']=-math.inf if data['previous_cutoff'] is None else data['previous_cutoff']
    data['intervals']=tuple(p.Interval(**{**r,'epoch':float(r['epoch'])}) for r in data['intervals']);data['audio']=tuple(data['audio'])
    snapshot=p.Snapshot(**data);p.preindex([r.path for r in snapshot.intervals]);plan=p.make_plan(snapshot)
    result=p.export(plan,sys.argv[2]);print(json.dumps(result,indent=2))

if __name__=='__main__':main()
