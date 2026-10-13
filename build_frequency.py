"""Builds english-frequency.txt, the word-frequency list embedded in the app.

Blends two public lists so neither one's quirks dominate:
  - conversational English: OpenSubtitles 2018 counts (hermitdave/FrequencyWords, en_50k.txt)
  - general written English: Google Web Trillion Word Corpus counts (norvig.com/ngrams/count_1w.txt)

Each word gets the geometric mean of its relative frequency in the two lists; a
word missing from one list is treated as being at that list's floor.

Usage: python -I build_frequency.py en_50k.txt count_1w.txt english-frequency.txt
"""
import math
import re
import sys

WEB_WORDS = 80000
PLAIN = re.compile(r"^[a-z]+$")


def read(path, limit=None):
    counts = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            parts = line.split()
            if len(parts) != 2 or not parts[1].isdigit():
                continue
            word = parts[0].lower()
            if word not in counts:
                counts[word] = int(parts[1])
            if limit and len(counts) >= limit:
                break
    return counts


def main(subs_path, web_path, out_path):
    subs = read(subs_path)
    web_all = read(web_path)
    web = dict(sorted(web_all.items(), key=lambda kv: -kv[1])[:WEB_WORDS])

    subs_total = sum(subs.values())
    web_total = sum(web_all.values())
    subs_floor = min(subs.values()) / subs_total
    web_floor = min(web.values()) / web_total

    merged = {}
    for word in set(subs) | set(web):
        s = subs[word] / subs_total if word in subs else None
        w = web[word] / web_total if word in web else None
        if not PLAIN.match(word):
            # Contraction pieces ('t, 'm) and the like exist only in the subtitle list.
            if s is None:
                continue
            f = s
        else:
            f = math.sqrt((s or subs_floor) * (w or web_floor))
        merged[word] = max(1, round(f * 1e9))

    with open(out_path, "w", encoding="utf-8", newline="\n") as out:
        for word, count in sorted(merged.items(), key=lambda kv: (-kv[1], kv[0])):
            out.write(f"{word} {count}\n")
    print(f"{len(merged)} words written to {out_path}")


if __name__ == "__main__":
    main(*sys.argv[1:4])
