"""Reads a Nivalis Customer Trace log (BepInEx\\cache\\NivalisCustomerTrace\\*.jsonl, newest by default) and reports
customer timelines, complaints with what triggered them, interaction odds against outcomes, reviews with their inputs,
staff task delays and delivery mismatches. Usage: python tools/customer_trace.py [log.jsonl] [--timelines] [--all]"""
import glob, json, os, sys
from collections import Counter, defaultdict

DIR = r"H:\SteamLibrary\steamapps\common\Nivalis Nights\BepInEx\cache\NivalisCustomerTrace"


def load(path):
    rows = []
    for line in open(path, encoding="utf-8"):
        line = line.strip()
        if line:
            try:
                rows.append(json.loads(line))
            except json.JSONDecodeError:
                pass
    return rows


def mins(a, b):
    """Game minutes between two events (gh = game hours)."""
    return None if a is None or b is None else round((b["gh"] - a["gh"]) * 60, 1)


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    path = args[0] if args else max(glob.glob(os.path.join(DIR, "*.jsonl")), key=os.path.getmtime)
    rows = load(path)
    # Each save load starts a segment (game time jumps back); report the last one, or --all for everything.
    segments, start = [], 0
    for i in range(1, len(rows)):
        if rows[i]["ev"] == "load" or rows[i]["gh"] < rows[i - 1]["gh"] - 0.01:
            segments.append(rows[start:i]); start = i
    segments.append(rows[start:])
    if "--all" not in sys.argv and len(segments) > 1:
        print(f"{len(segments)} save loads in this log; reporting the last (--all for every load)")
        rows = segments[-1]
    print(f"{os.path.basename(path)}: {len(rows)} events, {Counter(r['ev'] for r in rows).most_common()}\n")

    # ---- customers: first time each thing happened ----
    cust = defaultdict(lambda: {"events": []})
    for r in rows:
        if r["ev"] in ("arrive", "customer", "gone", "complaint", "interaction"):
            c = cust[r["id"]]
            c["events"].append(r)
            if r.get("name"): c["name"] = r["name"]
            if r.get("group"): c["group"] = r["group"]
    report = []
    for cid, c in cust.items():
        ev = c["events"]
        first = lambda pred: next((e for e in ev if pred(e)), None)
        arrive = ev[0]
        seated = first(lambda e: e.get("step") in ("WaitingToOrder", "WaitingForOrder", "Eating"))
        taken = first(lambda e: e.get("orderTaken"))
        prepared = first(lambda e: (e.get("order") or {}).get("prepared"))
        delivered = first(lambda e: (e.get("order") or {}).get("delivered"))
        eating = first(lambda e: e.get("step") == "Eating")
        gone = first(lambda e: e["ev"] == "gone")
        complaint = first(lambda e: e["ev"] == "complaint")
        inter = first(lambda e: e["ev"] == "interaction")
        last = next((e for e in reversed(ev) if e["ev"] == "customer"), arrive)
        order = last.get("order") or {}
        mismatched = [d["meal"] for d in order.get("delivered", []) if d["meal"] not in order.get("ordered", [])]
        report.append({
            "id": cid, "name": c.get("name"), "group": c.get("group"), "venue": arrive.get("venue"),
            "arrive": arrive["clock"], "toOrder": mins(seated, taken), "toPrepared": mins(taken, prepared),
            "toDelivered": mins(taken, delivered), "patienceAtDelivery": (delivered or {}).get("patience"),
            "faceAtDelivery": (delivered or {}).get("face"), "timerWait": ((taken or {}).get("timer") or {}).get("wait"),
            "stay": mins(arrive, gone), "complaint": (complaint or {}).get("reason"), "interaction": (inter or {}).get("choice"),
            "outcome": (inter or {}).get("outcome"), "ordered": order.get("ordered"), "delivered": [d["meal"] for d in order.get("delivered", [])],
            "fresh": [d["fresh"] for d in order.get("delivered", [])], "mismatched": mismatched, "lastStep": last.get("step"),
        })

    print(f"== Customers ({len(report)})")
    def avg(xs):
        xs = [x for x in xs if isinstance(x, (int, float))]
        return round(sum(xs) / len(xs), 1) if xs else None
    print(f"  game minutes, average: seated->order taken {avg(r['toOrder'] for r in report)}, "
          f"taken->first dish prepared {avg(r['toPrepared'] for r in report)}, taken->first dish delivered {avg(r['toDelivered'] for r in report)}, "
          f"whole visit {avg(r['stay'] for r in report)}")
    print(f"  face when food arrived: {Counter(r['faceAtDelivery'] for r in report if r['faceAtDelivery'])}")
    print(f"  patience timer lengths (game seconds): {Counter(r['timerWait'] for r in report if r['timerWait'])}")
    print(f"  left without food: {sum(1 for r in report if r['ordered'] and not r['delivered'] and r['stay'] is not None)}")
    mism = [r for r in report if r["mismatched"]]
    print(f"  wrong dish delivered: {len(mism)}" + "".join(f"\n    {r['name']}: ordered {r['ordered']}, got {r['delivered']}" for r in mism))

    # ---- complaints ----
    comps = [r for r in rows if r["ev"] == "complaint"]
    print(f"\n== Complaints ({len(comps)})  {Counter(c['reason'] for c in comps)}")
    for c in comps:
        seat = c.get("seat") or {}
        w = c.get("weather") or {}
        extra = "" if c.get("alreadyHad", "None") == "None" else f" (ignored: already {c['alreadyHad']})"
        print(f"  {c['clock']} {c['name']}: {c['reason']}{extra} | step {c['step']} patience {c['patience']} "
              f"fresh {c.get('avgFresh')} clean {c.get('cleanliness')} comfort t{seat.get('tableComfort')}/c{seat.get('chairComfort')} "
              f"cover {seat.get('cover')} rain {w.get('rain')} snow {w.get('snow')} staffHours {c.get('inStaffHours')}")

    # ---- interactions ----
    inters = [r for r in rows if r["ev"] == "interaction"]
    print(f"\n== Interactions ({len(inters)})")
    by = defaultdict(list)
    for i in inters:
        by[(i["choice"], i["complaint"] != "None")].append(i)
    for (choice, had), lst in sorted(by.items()):
        pos = sum(1 for i in lst if i["outcome"] == "Positive")
        print(f"  {choice:16} {'complaint' if had else 'no complaint':12} {pos}/{len(lst)} positive (game odds {lst[0]['chance']:.0%})")

    # ---- reviews ----
    revs = [r for r in rows if r["ev"] == "review"]
    print(f"\n== Reviews ({len(revs)})  scores {sorted(Counter(r['score'] for r in revs).items())}")
    complained = {c["name"] for c in comps if c.get("alreadyHad", "None") == "None"}
    for label, sel in (("complained", lambda r: r["by"] in complained), ("no complaint", lambda r: r["by"] not in complained)):
        s = [r["score"] for r in revs if sel(r)]
        if s: print(f"  {label}: {len(s)} reviews, average {sum(s) / len(s):.2f}, {sorted(Counter(s).items())}")
    for r in revs:
        meals = ", ".join(f"{m['meal']} q{m['quality']} f{m['fresh']}" for m in r.get("meals", []))
        print(f"  {r['clock']} {r['by']} ({r['group']}): {r['score']}* | taken {r['orderTaken']} allFood {r['allFood']} "
              f"service {r['service']} clean {r['cleanliness']} comfort {r['comfort']} | {r['interaction']}->{r['outcome']} | {meals}")

    # ---- staff ----
    delays = [r for r in rows if r["ev"] == "delay"]
    print(f"\n== Task retries ({len(delays)})  {Counter((d['kind'], d['delay']) for d in delays).most_common()}")
    # Appliances: what's placed, and what every station was doing at each retry.
    apps = [r for r in rows if r["ev"] == "appliances"]
    if apps:
        print("== Appliances (latest)")
        for a in apps[-1]["list"]:
            extra = f"type {a.get('type')} ({a.get('does')}) exclusivity {a.get('exclusivity')} calories {a.get('calories')}" if a["kind"] == "prep" else f"meals on it {a.get('mealsOn')}, recipes {a.get('recipes')}"
            print(f"  {a['kind']:4} {a.get('item')}: time {a.get('time')} | {extra}")
    withapps = [d for d in delays if d.get("appliances")]
    if withapps:
        print("== Station state at each retry")
        for d in withapps:
            meal = [a for a in d["appliances"] if a["kind"] == "meal"]
            busy = sum(1 for a in meal if a.get("inUse") or a.get("reservedBy"))
            sitting = sum(a.get("mealsOn") or 0 for a in meal)
            print(f"  {d['clock']} {d.get('meal')}: meal stations busy {busy}/{len(meal)}, finished meals waiting on them {sitting}; "
                  + ", ".join(f"{a.get('item')}={'busy:' + str(a.get('reservedBy')) if a.get('inUse') or a.get('reservedBy') else 'free'}" for a in d["appliances"]))
    tasks = [r for r in rows if r["ev"] == "task"]
    print(f"== Meals picked up by cooks: {len(tasks)}")
    staff = defaultdict(list)
    for r in rows:
        if r["ev"] == "staff": staff[r["name"]].append(r)
    print("== Staff time by action (game minutes)")
    for name, ev in staff.items():
        spent = Counter()
        for a, b in zip(ev, ev[1:]):
            spent[a.get("action") or "idle"] += (b["gh"] - a["gh"]) * 60
        print(f"  {name}: " + ", ".join(f"{k} {v:.0f}" for k, v in spent.most_common(6)))

    if "--timelines" in sys.argv:
        print("\n== Timelines")
        for r in report:
            print(f"  {r['arrive']} {r['name']} ({r['group']}): order {r['toOrder']}m, prepared +{r['toPrepared']}m, delivered +{r['toDelivered']}m "
                  f"(face {r['faceAtDelivery']}, patience {r['patienceAtDelivery']}), stay {r['stay']}m, {r['ordered']} -> {r['delivered']} "
                  f"fresh {r['fresh']}, complaint {r['complaint']}, {r['interaction']}->{r['outcome']}, last {r['lastStep']}")


if __name__ == "__main__":
    main()
