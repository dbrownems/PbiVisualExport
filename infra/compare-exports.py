"""Compares CSV exports of the LargeExportReport detail visual.

Checks the API export's integrity: every Row Id appears exactly once and the column count is
right. It can also compare the export cell by cell against
a second export, keyed on Row Id. The second export is usually Power BI's native export
(visual.exportData, capped at 30k rows), or the API export in the other format mode.

    python infra/compare-exports.py api.csv                      # integrity only
    python infra/compare-exports.py api.csv native.csv           # + cell comparison
    python infra/compare-exports.py api.csv native.csv --expect-rows 2000000

Only the rows present in the (smaller) second file are held in memory, so a 2M-row API
file is streamed.
"""
import argparse
import csv
import sys
from collections import Counter, defaultdict

csv.field_size_limit(2**31 - 1)
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
KEY = "Row Id"


def open_csv(path):
    # utf-8-sig strips the BOM the API (and Excel-style exports) write.
    f = open(path, encoding="utf-8-sig", newline="")
    return f, csv.reader(f)


def key_of(value: str) -> int:
    return int(value.replace(",", "").strip())


def load_small(path):
    f, rdr = open_csv(path)
    with f:
        header = next(rdr)
        if KEY not in header:
            sys.exit(f"{path}: no '{KEY}' column; header = {header}")
        k = header.index(KEY)
        rows, dupes = {}, 0
        for r in rdr:
            kv = key_of(r[k])
            dupes += kv in rows
            rows[kv] = r
    return header, rows, dupes


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("api")
    ap.add_argument("other", nargs="?")
    ap.add_argument("--expect-rows", type=int)
    ap.add_argument("--samples", type=int, default=3)
    a = ap.parse_args()

    other_header, other_rows, other_dupes = (None, {}, 0)
    if a.other:
        other_header, other_rows, other_dupes = load_small(a.other)

    f, rdr = open_csv(a.api)
    with f:
        header = next(rdr)
        k = header.index(KEY)
        ncols = len(header)
        seen = bytearray()
        count = dupes = bad_width = 0
        min_k, max_k = None, None
        matched = 0
        shared = [c for c in header if other_header and c in other_header]
        oidx = {c: other_header.index(c) for c in shared} if other_header else {}
        aidx = {c: header.index(c) for c in shared}
        mismatches = Counter()
        samples = defaultdict(list)
        for r in rdr:
            count += 1
            if len(r) != ncols:
                bad_width += 1
                continue
            kv = key_of(r[k])
            if kv >= len(seen):
                seen.extend(b"\0" * max(kv + 1 - len(seen), 1 << 20))
            if seen[kv]:
                dupes += 1
            seen[kv] = 1
            min_k = kv if min_k is None else min(min_k, kv)
            max_k = kv if max_k is None else max(max_k, kv)
            o = other_rows.get(kv)
            if o is not None:
                matched += 1
                for c in shared:
                    av, ov = r[aidx[c]], o[oidx[c]]
                    if av != ov:
                        mismatches[c] += 1
                        if len(samples[c]) < a.samples:
                            samples[c].append((kv, av, ov))

    distinct = sum(seen)
    print(f"API file:   {a.api}")
    print(f"  rows={count:,} distinct {KEY}={distinct:,} duplicates={dupes:,} "
          f"bad-width rows={bad_width:,} columns={ncols}")
    print(f"  {KEY} range {min_k}..{max_k}")
    if min_k is not None and max_k is not None:
        missing = (max_k - min_k + 1) - distinct
        print(f"  gaps inside range: {missing:,}")
    if a.expect_rows is not None:
        ok = count == a.expect_rows == distinct and dupes == 0 and bad_width == 0
        print(f"  expected {a.expect_rows:,} rows -> {'OK' if ok else 'MISMATCH'}")

    if a.other:
        print(f"\nOther file: {a.other}")
        print(f"  rows={len(other_rows):,} duplicates={other_dupes} columns={len(other_header)}")
        only_api = [c for c in header if c not in other_header]
        only_other = [c for c in other_header if c not in header]
        if only_api:
            print(f"  columns only in API:   {only_api}")
        if only_other:
            print(f"  columns only in other: {only_other}")
        print(f"  rows matched on {KEY}: {matched:,} of {len(other_rows):,}")
        print(f"\nCell comparison over {len(shared)} shared columns:")
        for c in shared:
            n = mismatches[c]
            line = f"  {c:<18} {'identical' if n == 0 else f'{n:,} differ'}"
            print(line)
            for kv, av, ov in samples[c]:
                print(f"      {KEY}={kv}: api={av!r}  other={ov!r}")


if __name__ == "__main__":
    main()
