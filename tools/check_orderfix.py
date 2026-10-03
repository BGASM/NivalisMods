#!/usr/bin/env python3
"""Check Order Fix verbose logs for internal consistency.

Usage: tools/check_orderfix.py LOG [LOG ...] [--hops-ref REF_LOG ...]

For each log:
  1. Sort order: within each item's candidate list, the logged vendor order must match
     the mode's rule from Order Fix Order(). Exact ties are allowed in either order.
     The mode comes from each item's Buy lines, so switching VendorSort mid-session is fine.
  2. Balanced score: price * (1 + dw*hops) * (1 + sw/max(stock,1)) matches the logged score.
  3. Buy/skip: buys come before skips in an item's list, and no Buy follows a Skip.
  4. Hops: every (venue, vendor) hops value agrees with every other log given
     (including --hops-ref logs), since hops don't depend on prices.
  5. Startup: the "Hooked BuyItem" line is printed and identical across logs.
"""
import re
import sys
from collections import defaultdict

DW, SW = 0.07, 2.0

START = re.compile(r"Kit Tester\] BuyIngredientsStarting: (.+?) / (.+)$")
FINISH = re.compile(r"Kit Tester\] BuyIngredientsFinished: ")
BUY = re.compile(r"Manager Order Fix(?: \(Standalone\))?\] Buy (.+?) x(\d+) at (.+?) \(price (\d+), stock (\d+), "
                 r"hops (\d+), score (\d+)\) \[(\w+), (\d+) vendors\]")
SKIP = re.compile(r"Manager Order Fix(?: \(Standalone\))?\] Skip (.+?) at (.+?) \(price (\d+), stock (\d+), hops (\d+)\): order filled")
FAIL = re.compile(r"Manager Order Fix(?: \(Standalone\))?\] Purchase failed for (.+?) at (.+?) \(")
# Order Fix 1.x hooked BuyItem itself; from 2.0 the kit's purchasing pipeline prints the line.
HOOK = re.compile(r"(?:Manager Order Fix(?: \(Standalone\))?|Nivalis ModKit)\] Hooked (?:BuyItem at \S+ |(GetVendorsByItem): )(.*)$")
MODE = re.compile(r"Manager Order Fix(?: \(Standalone\))?\] Manager Order Fix loaded, VendorSort = (\w+)")


def sort_key(mode, c):
    # Mirrors Order() in NivalisOrderFix 1.0; Seq (offer order) isn't logged, so it's
    # left out and exact ties are accepted in either order.
    if mode == "Cheapest":
        return (c["price"], -c["stock"])
    if mode == "Local":
        return (c["hops"], -c["stock"], c["price"])
    if mode == "Balanced":
        return (score(c), -c["stock"])
    return (-c["stock"],)  # Vanilla


def score(c):
    return c["price"] * (1 + DW * c["hops"]) * (1 + SW / max(c["stock"], 1))


def parse(path):
    rounds, cur, mode, hook = [], None, None, None
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.rstrip("\n")
            if m := MODE.search(line):
                mode = m.group(1)
            elif m := HOOK.search(line):
                hook = f"{m.group(1) or 'BuyItem'}: {m.group(2)}"
            elif m := START.search(line):
                cur = {"venue": m.group(1), "recipe": m.group(2), "items": defaultdict(list)}
                rounds.append(cur)
            elif cur is None:
                continue
            elif m := BUY.search(line):
                item, n, vendor, price, stock, hops, sc, lmode, nv = m.groups()
                cur["items"][item].append(dict(act="buy", vendor=vendor, n=int(n), price=int(price),
                    stock=int(stock), hops=int(hops), score=int(sc), mode=lmode, line=line))
            elif m := SKIP.search(line):
                item, vendor, price, stock, hops = m.groups()
                cur["items"][item].append(dict(act="skip", vendor=vendor, price=int(price),
                    stock=int(stock), hops=int(hops), line=line))
            elif m := FAIL.search(line):
                cur["items"][m.group(1)].append(dict(act="fail", vendor=m.group(2), line=line))
            elif FINISH.search(line):
                cur = None
    return mode, hook, rounds


def check(path, hops_table, hooks):
    mode, hook, rounds = parse(path)
    errors, decisions = [], 0
    current, mode_counts = None, defaultdict(int)
    hooks[path] = hook
    if hook is None:
        errors.append("no 'Hooked BuyItem' or 'Hooked GetVendorsByItem' line (is Verbose on?)")

    for r in rounds:
        where = f"{r['venue']} / {r['recipe']}"
        for item, cands in r["items"].items():
            priced = [c for c in cands if c["act"] in ("buy", "skip")]
            decisions += len(priced)

            # The mode can change mid-session (VendorSort re-read each round), so take it from
            # the item's Buy lines; an item with only Skips keeps the last mode seen.
            buy_modes = {c["mode"] for c in cands if c["act"] == "buy"}
            if len(buy_modes) > 1:
                errors.append(f"[mode] {where} {item}: Buy lines disagree: {sorted(buy_modes)}")
            if buy_modes:
                current = min(buy_modes)
            item_mode = current or mode
            mode_counts[item_mode] += 1

            # 1. sort order (non-decreasing key)
            for a, b in zip(priced, priced[1:]):
                if sort_key(item_mode, a) > sort_key(item_mode, b):
                    errors.append(f"[order] {where} {item}: {a['vendor']} before {b['vendor']}\n"
                                  f"    {a['line'].split('] ',1)[1]}\n    {b['line'].split('] ',1)[1]}")

            # 2. balanced score (Order Fix prints score rounded with :0)
            for c in priced:
                if c["act"] == "buy" and abs(score(c) - c["score"]) > 0.5 + 1e-6:
                    errors.append(f"[score] {where} {item} at {c['vendor']}: logged {c['score']}, "
                                  f"expected {score(c):.2f}")

            # 3. no buy after skip
            seen_skip = False
            for c in cands:
                if c["act"] == "skip":
                    seen_skip = True
                elif c["act"] == "buy" and seen_skip:
                    errors.append(f"[flow] {where} {item}: Buy at {c['vendor']} after a Skip")

            # 4. hops by (venue, vendor)
            for c in priced:
                key = (r["venue"], c["vendor"])
                hops_table[key].add((c["hops"], path))

    modes = ", ".join(f"{m} {n}" for m, n in sorted(mode_counts.items()))
    return f"items by mode: {modes}", len(rounds), decisions, errors


def main(argv):
    refs, logs = [], []
    target = logs
    for a in argv:
        if a == "--hops-ref":
            target = refs
        else:
            target.append(a)
    if not logs:
        print(__doc__)
        return 2

    hops_table, hooks, failed = defaultdict(set), {}, False
    for path in refs:
        check(path, hops_table, hooks)

    for path in logs:
        mode, n_rounds, n_dec, errors = check(path, hops_table, hooks)
        status = "PASS" if not errors else f"FAIL ({len(errors)})"
        print(f"{status}  {path}: {mode}, {n_rounds} rounds, {n_dec} vendor decisions")
        for e in errors[:20]:
            print("  " + e)
        if len(errors) > 20:
            print(f"  ... {len(errors) - 20} more")
        failed |= bool(errors)

    # 4. hops agreement across all logs
    bad = {k: v for k, v in hops_table.items() if len({h for h, _ in v}) > 1}
    pairs = len(hops_table)
    if bad:
        failed = True
        print(f"FAIL  hops: {len(bad)} of {pairs} (venue, vendor) pairs disagree")
        for (venue, vendor), v in list(bad.items())[:20]:
            print(f"  {venue} -> {vendor}: " + ", ".join(f"{h} in {p}" for h, p in sorted(v)))
    else:
        print(f"PASS  hops: {pairs} (venue, vendor) pairs agree across logs")

    # 5. startup line identical
    # compared per edition: the kit hooks BuyItem, the standalone GetVendorsByItem
    by_kind = defaultdict(set)
    for h in hooks.values():
        if h is not None:
            by_kind[h.split(":", 1)[0]].add(h)
    for kind, distinct in sorted(by_kind.items()):
        if len(distinct) > 1:
            failed = True
            print(f"FAIL  startup ({kind}) differs:")
            for p, h in hooks.items():
                if h and h.startswith(kind + ":"):
                    print(f"  {p}: {h}")
        else:
            print(f"PASS  startup: {distinct.pop()}")

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
