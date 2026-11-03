"""Builds data/fixes-open-source.txt from open misspelling lists in third_party/.

Two steps, because the app itself decides which pairs are worth keeping:

  python -I tools/build_open_fixes.py prepare candidates.txt
      Reads the sources and writes "target: typo typo ..." lines, the format of
      `KeyboardWriterHelper.exe --eval`.

  KeyboardWriterHelper.exe --eval --misses candidates.txt > misses.txt
      The app corrects what it can. What is left are the misses.

  python -I tools/build_open_fixes.py merge misses.txt data/fixes-open-source.txt
      Keeps the misses that are safe to add as built-in fixes: the typed word is
      not a word Windows accepts, it is close to the intended word, and it has
      one clear answer. Words from the Norvig test sets are left out so those
      can still measure the app honestly.

Sources (see THIRD_PARTY_NOTICES.md for licences and credit):
  Wikipedia "Lists of common misspellings"      CC BY-SA 4.0
  Birkbeck spelling error corpus (R. Mitton)    free to use, credit required
  Norvig spell-errors.txt (from Birkbeck data)  MIT, Peter Norvig
  LibreOffice AutoCorrect, en-US                MPL 2.0
  GitHub Typo Corpus (Hagiwara and Mita)       terms of the source repositories
"""
import os
import re
import sys
import xml.etree.ElementTree as ET
import html

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TP = os.path.join(ROOT, "third_party")
WORD = re.compile(r"^[a-z]+$")
TYPO = re.compile(r"^[a-z]+$")


def edit_distance(a, b, limit=3):
    """Damerau-Levenshtein (adjacent swaps count once)."""
    if abs(len(a) - len(b)) > limit:
        return limit + 1
    prev2 = None
    prev = list(range(len(b) + 1))
    for i in range(1, len(a) + 1):
        cur = [i] + [0] * len(b)
        for j in range(1, len(b) + 1):
            cost = 0 if a[i - 1] == b[j - 1] else 1
            cur[j] = min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + cost)
            if i > 1 and j > 1 and a[i - 1] == b[j - 2] and a[i - 2] == b[j - 1]:
                cur[j] = min(cur[j], prev2[j - 2] + 1)
        prev2, prev = prev, cur
    return prev[len(b)]


def read(path):
    with open(path, encoding="utf-8", errors="replace") as f:
        return f.read()


def wikipedia():
    pairs = []
    for line in read(os.path.join(TP, "wikipedia", "misspellings-for-machines.txt")).splitlines():
        if "->" not in line:
            continue
        typo, _, target = line.strip().partition("->")
        if "," in target:  # more than one possible answer
            continue
        pairs.append((typo.strip().lower(), target.strip().lower(), "wikipedia"))
    return pairs


def birkbeck():
    pairs = []
    folder = os.path.join(TP, "birkbeck")
    for name in sorted(os.listdir(folder)):
        target = None
        for line in read(os.path.join(folder, name)).splitlines():
            line = line.strip()
            if not line:
                continue
            if line.startswith("$"):
                target = line[1:].strip().lower()
            elif target:
                pairs.append((line.split()[0].lower(), target, "birkbeck"))
    return pairs


def norvig():
    pairs = []
    for line in read(os.path.join(TP, "norvig", "spell-errors.txt")).splitlines():
        target, _, rest = line.partition(":")
        for item in rest.split(","):
            typo = item.strip().split("*")[0].strip().lower()
            if typo:
                pairs.append((typo, target.strip().lower(), "norvig"))
    return pairs


def libreoffice():
    pairs = []
    root = ET.fromstring(read(os.path.join(TP, "libreoffice", "DocumentList.xml")).encode("utf-8"))
    for block in root:
        typo = html.unescape(block.attrib.get("{http://openoffice.org/2001/block-list}abbreviated-name", "")).lower()
        target = html.unescape(block.attrib.get("{http://openoffice.org/2001/block-list}name", "")).lower()
        pairs.append((typo, target, "libreoffice"))
    return pairs


def github_typo(min_count=2):
    """Word pairs from the GitHub Typo Corpus: lines of a commit that changed
    exactly one word and were judged real typo fixes, seen at least twice."""
    import gzip
    import json
    path = os.path.join(TP, "github-typo-corpus", "github-typo-corpus.v1.0.0.jsonl.gz")
    if not os.path.exists(path):
        return []
    token = re.compile(r"[A-Za-z']+|[^A-Za-z']+")
    counts = {}
    with gzip.open(path, "rt", encoding="utf-8") as f:
        for line in f:
            for edit in json.loads(line).get("edits", []):
                if not edit.get("is_typo") or edit["src"].get("lang") != "eng":
                    continue
                a, b = token.findall(edit["src"]["text"]), token.findall(edit["tgt"]["text"])
                if len(a) != len(b):
                    continue
                diff = [(x, y) for x, y in zip(a, b) if x != y]
                if len(diff) != 1:
                    continue
                typo, fix = diff[0][0].lower(), diff[0][1].lower()
                if TYPO.match(typo) and WORD.match(fix):
                    counts[(typo, fix)] = counts.get((typo, fix), 0) + 1
    return [(t, f, "github-typo") for (t, f), n in counts.items() if n >= min_count]


def holdout():
    """The Norvig test sets: never added, so they stay a fair test."""
    words = set()
    for name in ("spell-testset1.txt", "spell-testset2.txt"):
        for line in read(os.path.join(TP, "norvig", name)).splitlines():
            target, _, rest = line.partition(":")
            for item in rest.split():
                words.add(item.strip(",").split("*")[0].lower())
    return words


def candidates():
    seen = {}
    for typo, target, source in wikipedia() + libreoffice() + birkbeck() + norvig() + github_typo():
        if not TYPO.match(typo) or not WORD.match(target) or len(typo) < 4 or typo == target:
            continue
        # Far-off pairs are usually noise, or a different word altogether.
        limit = 1 if len(typo) < 6 else 2
        if edit_distance(typo, target) > limit:
            continue
        seen.setdefault(typo, {}).setdefault(target, set()).add(source)
    return seen


def frequencies():
    counts = {}
    with open(os.path.join(ROOT, "english-frequency.txt"), encoding="utf-8") as f:
        for line in f:
            parts = line.split()
            if len(parts) == 2 and parts[1].isdigit():
                counts[parts[0]] = int(parts[1])
    return counts


# Sources checked by people or built from real handwriting; the GitHub corpus
# is full of code identifiers, so on its own it is not enough.
CURATED = {"wikipedia", "norvig", "birkbeck", "libreoffice"}
MIN_COMMON = 500


def prepare(out_path):
    by_target = {}
    for typo, targets in candidates().items():
        for target in targets:
            by_target.setdefault(target, []).append(typo)
    lines = ["%s: %s" % (t, " ".join(sorted(v))) for t, v in sorted(by_target.items())]
    with open(out_path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")
    print("%d typos for %d words written to %s" % (sum(len(v) for v in by_target.values()), len(by_target), out_path))


MISS = re.compile(r"^\s*miss: (\S+) -> (.*?)   wanted (\S+)   windows: (.*)$")


def merge(misses_path, out_path):
    cands = candidates()
    held = holdout()
    never = set()
    for name in ("never-correct.txt", "other-language.txt"):
        for line in read(os.path.join(ROOT, "data", name)).splitlines():
            if not line.startswith("#"):
                never.update(line.split())
    existing = set()
    for name in ("fixes.txt", "phrase-fixes.txt"):
        for line in read(os.path.join(ROOT, "data", name)).splitlines():
            if "=" in line and not line.startswith("#"):
                existing.add(line.split("=", 1)[0].strip().lower())
    protected = set()
    for name in ("correct-words.txt", "typos.txt", "reviewer-typos.txt"):
        pass
    for name in ("correct-words.txt",):
        for line in read(os.path.join(ROOT, "tests", name)).splitlines():
            if ":" in line:
                protected.update(line.split(":", 1)[1].split())

    common = frequencies()
    kept = {}
    skipped = {"accepted by Windows": 0, "held out": 0, "ambiguous": 0, "other": 0, "not convincing": 0}
    for line in read(misses_path).splitlines():
        m = MISS.match(line)
        if not m:
            continue
        typo, got, wanted, windows = m.group(1), m.group(2), m.group(3), m.group(4)
        if typo not in cands or wanted not in cands[typo]:
            continue
        if windows.strip() == "(accepts it)":
            skipped["accepted by Windows"] += 1  # a real word: never "fix" it
            continue
        if typo in held:
            skipped["held out"] += 1
            continue
        if len(cands[typo]) > 1:
            skipped["ambiguous"] += 1  # two different answers in the sources
            continue
        if typo in never or typo in existing or typo in protected:
            skipped["other"] += 1
            continue
        sources = cands[typo][wanted]
        offered = [w.strip().lower() for w in windows.split(",")]
        # A real, common word, and either Windows itself offers it (the Ranker
        # only chose another) or two checked sources agree on a close typo.
        convincing = common.get(wanted, 0) >= MIN_COMMON and (
            wanted in offered or
            (len(sources & CURATED) >= 2 and edit_distance(typo, wanted) <= 1 and len(typo) >= 5))
        if not convincing:
            skipped["not convincing"] += 1
            continue
        kept[typo] = (wanted, sorted(sources))

    with open(out_path, "w", encoding="utf-8", newline="\r\n") as f:
        f.write("# Built-in fixes from open misspelling lists, one per line: typo=correction\n")
        f.write("# Generated by tools/build_open_fixes.py. Do not edit by hand: change the tool or\n")
        f.write("# add the fix to fixes.txt, which wins over this file.\n")
        f.write("# Only misspellings the app did not already correct are listed, and never a word\n")
        f.write("# that Windows accepts. Sources and credit: THIRD_PARTY_NOTICES.md.\n\n")
        for typo in sorted(kept):
            f.write("%s=%s\n" % (typo, kept[typo][0]))
    print("%d fixes written to %s" % (len(kept), out_path))
    print("skipped: " + ", ".join("%s %d" % kv for kv in skipped.items()))


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "prepare":
        prepare(sys.argv[2])
    elif len(sys.argv) == 4 and sys.argv[1] == "merge":
        merge(sys.argv[2], sys.argv[3])
    else:
        print(__doc__)
        sys.exit(2)
