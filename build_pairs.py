"""Builds english-pairs.txt, the word-pair counts embedded in the app.

The app uses them to let the previous word help choose between candidate
corrections ("more thn" -> "than", "and thn" -> "then") and to spot a real word
used in the wrong place ("got it form" -> "from").

Source: Google Web Trillion Word Corpus two-word counts (norvig.com/ngrams/count_2w.txt,
the 1/4 million most frequent pairs). Pairs are lower-cased and kept only when
both words are plain a-z words of the app's own frequency list.

Usage: python -I build_pairs.py count_2w.txt english-frequency.txt english-pairs.txt
"""
import re
import sys

PLAIN = re.compile(r"^[a-z]+$")


def main(pairs_path, words_path, out_path):
    with open(words_path, encoding="utf-8") as f:
        known = {line.split()[0] for line in f if line.strip()}

    merged = {}
    with open(pairs_path, encoding="utf-8") as f:
        for line in f:
            parts = line.split()
            if len(parts) != 3 or not parts[2].isdigit():
                continue
            first, second = parts[0].lower(), parts[1].lower()
            if not (PLAIN.match(first) and PLAIN.match(second)):
                continue
            if first not in known or second not in known:
                continue
            key = (first, second)
            merged[key] = merged.get(key, 0) + int(parts[2])

    with open(out_path, "w", encoding="utf-8", newline="\n") as out:
        for (first, second), count in sorted(merged.items(), key=lambda kv: (-kv[1], kv[0])):
            out.write(f"{first} {second} {count}\n")
    print(f"{len(merged)} pairs written to {out_path}")


if __name__ == "__main__":
    main(*sys.argv[1:4])
