# Third-party data and credit

Keyboard Writer Helper's word data comes from the open projects below. Thank you
to everyone who built and shared them. The raw files are in `third_party/`; what
the app uses is built from them by the tools named in each row, and every entry
is checked against the app's own tests before it is kept.

| Source | What we use it for | Credit | Licence / terms |
|---|---|---|---|
| **Wikipedia: Lists of common misspellings** (machine-readable) | Misspelling → correct word pairs (`data/fixes-open-source.txt`) | Wikipedia contributors, <https://en.wikipedia.org/wiki/Wikipedia:Lists_of_common_misspellings> | CC BY-SA 4.0. Pairs derived from it are shared under the same licence. |
| **Birkbeck spelling error corpus**, files `missp.dat`, `aspell.dat`, `wikipedia.dat`, `holbrook-missp.dat` | Real human misspellings of words (`data/fixes-open-source.txt`) | Roger Mitton, Birkbeck, University of London; corpora page <https://www.dcs.bbk.ac.uk/~ROGER/corpora.html>; Oxford Text Archive <http://ota.ahds.ac.uk/texts/0643.html>; the Holbrook passages are from David Holbrook, *English for the Rejected* (CUP, 1964) | Offered for free download for others to use, with credit. No formal licence text is published; check with the authors before any commercial redistribution. |
| **Peter Norvig, `spell-errors.txt`, `spell-testset1.txt`, `spell-testset2.txt`** | More misspelling pairs; the two test sets are kept out of the built-in fixes and used only to measure accuracy | Peter Norvig, <https://norvig.com/spell-correct.html> (data built on the Birkbeck corpus above) | Norvig's code is MIT-licensed; the data files carry no separate licence text. Credit is given as he and the Birkbeck authors ask. |
| **LibreOffice AutoCorrect, en-US** (`DocumentList.xml`) | Typing shortcuts and fixes | The Document Foundation and LibreOffice contributors, <https://github.com/LibreOffice/core> | MPL 2.0 |
| **SymSpell frequency dictionaries** (`frequency_dictionary_en_82_765.txt`, `frequency_bigramdictionary_en_243_342.txt`) | Word and word-pair frequencies, as a cross-check of `english-frequency.txt` and `english-pairs.txt` | Wolf Garbe, <https://github.com/wolfgarbe/SymSpell>; the counts come from the Google Books Ngram data and the SCOWL word list | MIT (see `third_party/symspell/LICENSE`) |
| **Hunspell dictionaries** `en`, `en-GB`, `en-AU`, `en-CA` | Word lists to tell real words from typos | The dictionaries are from the `wooorm/dictionaries` collection (<https://github.com/wooorm/dictionaries>); the English word list is SCOWL by Kevin Atkinson, <http://wordlist.aspell.net/>, with Hunspell by László Németh | Permissive, MIT-style SCOWL / Hunspell licence terms; each folder's `*.license` file has the exact text. |
| **LanguageTool English grammar rules** (`grammar.xml`, not committed because of its size: 8 MB) | A reference for writing our own grammar rules in `data/` and `src/Engine/` | LanguageTool contributors, <https://github.com/languagetool-org/languagetool> | LGPL 2.1 (`third_party/languagetool/COPYING.txt`). Rules are re-written as plain data, never copied as code. |
| **GitHub Typo Corpus** v1.0.0 (203,270 commits; the 44 MB file is not committed) | One-word typo fixes that were seen at least twice become candidate pairs for `data/fixes-open-source.txt`. Only the derived word pairs are used, never the source text | Masato Hagiwara and Masato Mita, *GitHub Typo Corpus: A Large-Scale Multilingual Dataset of Misspellings and Grammatical Errors* (LREC 2020), <https://github.com/mhagiwara/github-typo-corpus>. The original download link is gone, so the file came from the Internet Archive's copy of it | The texts follow the licences of the GitHub repositories they came from (see `third_party/github-typo-corpus/README.md`). We keep only single-word pairs, not code or sentences. |
| **Dakshina Dataset** (Google Research) | Not downloaded: the full file is 2 GB and was cancelled. Listed as a possible later source for Hindi in Latin letters | Roy et al., *Processing South Asian Languages Written in the Latin Script: the Dakshina Dataset* (LREC 2020), <https://github.com/google-research-datasets/dakshina> | CC BY-SA 4.0 |
| **CMU Pronouncing Dictionary** | Reference for sound-alike checks (kept for a later step, not used yet) | Carnegie Mellon University, <https://github.com/cmusphinx/cmudict> | BSD-style (`third_party/cmudict/LICENSE`) |
| **ENABLE word list** | A second list of real words, used when checking that a typo is not itself a word | Alan Beale and contributors, via <https://github.com/dolph/dictionary> | Public domain |
| **SCOWL VarCon** (American / British / Canadian / Australian variants) | Reference for words that are spelled differently but are both right ("color" / "colour") | Kevin Atkinson, <http://wordlist.aspell.net/varcon/> | SCOWL licence (`third_party/scowl-varcon/Copyright`) |
| **OpenSubtitles 2018 frequency counts** (already used) | `english-frequency.txt` | hermitdave / FrequencyWords, <https://github.com/hermitdave/FrequencyWords> | CC BY-SA 4.0 |
| **Google Web Trillion Word Corpus counts** (already used) | `english-frequency.txt`, `english-pairs.txt` | Peter Norvig, <https://norvig.com/ngrams/> | Counts from Google's corpus, published by Norvig at the page above; his code there is MIT-licensed. |

## How the built-in fixes are made

`tools/build_open_fixes.py` reads the misspelling lists, keeps pairs that are
close in spelling (one or two letters off), and writes them as candidates. The
app is run over them (`--eval --misses`), and only the ones it still got wrong are
added to `data/fixes-open-source.txt`. A typed word that Windows accepts as a real
word is never added, a word with two different answers is not added, and the
Norvig test sets are left out so they can measure the result honestly.

## Share-alike note

Pairs taken from Wikipedia are CC BY-SA 4.0, so `data/fixes-open-source.txt`
carries that licence with the credit above. The rest of the program is under the
repository's own licence.
