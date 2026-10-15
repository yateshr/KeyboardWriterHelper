# tools/expand_corpus.py
# Enriches english-frequency.txt, english-pairs.txt, generates english-trigrams.txt,
# and packs everything into a fast binary model english-model.bin.

import os
import sys
import math
import struct

def main():
    base_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    freq_file = os.path.join(base_dir, "english-frequency.txt")
    pairs_file = os.path.join(base_dir, "english-pairs.txt")
    trigrams_file = os.path.join(base_dir, "english-trigrams.txt")
    model_bin = os.path.join(base_dir, "english-model.bin")

    print(f"Base directory: {base_dir}")

    # 1. Load existing frequency words
    freq_dict = {}
    if os.path.exists(freq_file):
        with open(freq_file, "r", encoding="utf-8") as f:
            for line in f:
                parts = line.strip().split()
                if len(parts) >= 2:
                    try:
                        count = int(parts[1])
                        freq_dict[parts[0]] = count
                    except ValueError:
                        continue

    print(f"Loaded {len(freq_dict)} existing frequency words.")

    # Modern tech, developer, internet, corporate and daily words to ensure in vocabulary
    new_words = {
        # Tech & Development
        "docker": 18500000, "kubernetes": 12400000, "github": 26000000, "vscode": 9800000,
        "frontend": 14200000, "backend": 15600000, "microservices": 8200000, "devops": 11300000,
        "typescript": 17500000, "javascript": 24000000, "csharp": 8500000, "golang": 7200000,
        "graphql": 9100000, "oauth": 11500000, "webhook": 10200000, "webhooks": 7500000,
        "middleware": 10800000, "nginx": 14000000, "redis": 15200000, "mongodb": 13900000,
        "postgresql": 14500000, "sqlite": 12100000, "pipeline": 13400000, "pipelines": 8200000,
        "refactor": 7800000, "refactoring": 6500000, "endpoint": 12800000, "endpoints": 10500000,
        "async": 18200000, "await": 17800000, "lambda": 9500000, "payload": 11200000,
        "config": 19400000, "configs": 6800000, "runtime": 12600000, "compiler": 8900000,
        "serverless": 8100000, "container": 14000000, "containers": 11200000, "cluster": 12300000,
        "clusters": 8700000, "telemetry": 6200000, "dashboard": 15800000, "metadata": 14100000,
        "param": 9200000, "params": 11400000, "arg": 6800000, "args": 13200000,
        "repo": 16500000, "repos": 9400000, "commit": 18900000, "commits": 12100000,
        "rebase": 5800000, "checkout": 14500000, "branch": 17800000, "branches": 10200000,
        "merge": 16200000, "pull": 21500000, "push": 22100000, "stash": 6500000,
        "staging": 9800000, "production": 24500000, "sandbox": 8500000, "prod": 12400000,
        "debug": 15400000, "debugger": 7600000, "lint": 5200000, "linter": 4800000,
        "boilerplate": 4900000, "scaffold": 4200000, "transpile": 3100000, "minified": 3800000,
        "json": 28500000, "yaml": 12500000, "toml": 4500000, "markdown": 14200000,
        "auth": 17800000, "token": 19800000, "tokens": 12400000, "jwt": 8900000,
        "session": 19500000, "cookie": 16200000, "cookies": 14100000, "cors": 7800000,
        "restful": 6500000, "grpc": 5800000, "websocket": 8900000, "websockets": 6400000,
        "schema": 15600000, "schemas": 8900000, "migration": 9800000, "migrations": 8400000,
        "orm": 6200000, "query": 22500000, "queries": 14200000, "crud": 5400000,

        # Indian & Regional English
        "prepone": 4500000, "preponed": 3800000, "preponing": 2900000, "updation": 4200000,
        "swiggy": 8500000, "zomato": 8400000, "aadhaar": 9800000, "aadhar": 8200000,
        "paytm": 9500000, "phonepe": 8900000, "gpay": 8100000, "bhai": 7800000,
        "yaar": 6500000, "jugaad": 4800000, "theek": 5200000, "accha": 4900000,

        # Business, Collaboration & Common Modern Terms
        "actionable": 7800000, "deliverable": 8200000, "deliverables": 9400000,
        "stakeholder": 9800000, "stakeholders": 12400000, "onboarding": 8900000,
        "offboarding": 4200000, "touchpoint": 5100000, "touchpoints": 4800000,
        "bandwidth": 10500000, "roadmap": 12800000, "synergy": 6800000, "streamline": 7900000,
        "streamlined": 8400000, "scalability": 9800000, "workflow": 16800000, "workflows": 12100000,
        "playbook": 6500000, "painpoint": 5400000, "painpoints": 5900000, "upskill": 4800000
    }

    added_words = 0
    for w, c in new_words.items():
        if w not in freq_dict:
            freq_dict[w] = c
            added_words += 1

    print(f"Added {added_words} new modern/tech/regional words to vocabulary.")

    # Write back sorted frequency file
    sorted_words = sorted(freq_dict.items(), key=lambda kv: kv[1], reverse=True)
    with open(freq_file, "w", encoding="utf-8") as f:
        for w, c in sorted_words:
            f.write(f"{w} {c}\n")

    # 2. Enrich english-pairs.txt with tech and conversational bigrams
    pair_dict = {}
    if os.path.exists(pairs_file):
        with open(pairs_file, "r", encoding="utf-8") as f:
            for line in f:
                parts = line.strip().split()
                if len(parts) >= 3:
                    try:
                        count = int(parts[2])
                        pair_dict[(parts[0], parts[1])] = count
                    except ValueError:
                        continue

    print(f"Loaded {len(pair_dict)} existing bigram pairs.")

    new_pairs = {
        # Git & Dev
        ("git", "commit"): 45000000, ("git", "push"): 48000000, ("git", "pull"): 42000000,
        ("git", "checkout"): 39000000, ("git", "branch"): 36000000, ("git", "status"): 38000000,
        ("git", "merge"): 32000000, ("git", "add"): 41000000, ("git", "rebase"): 28000000,
        ("pull", "request"): 65000000, ("pull", "requests"): 32000000, ("code", "review"): 48000000,
        ("merge", "request"): 28000000, ("unit", "test"): 34000000, ("unit", "tests"): 42000000,
        ("integration", "tests"): 29000000, ("test", "suite"): 31000000, ("bug", "fix"): 36000000,
        ("breaking", "change"): 26000000, ("breaking", "changes"): 28000000,

        # Node / NPM / Docker
        ("npm", "install"): 48000000, ("npm", "run"): 39000000, ("npm", "start"): 34000000,
        ("npm", "test"): 28000000, ("npm", "build"): 26000000, ("npm", "publish"): 22000000,
        ("docker", "run"): 38000000, ("docker", "build"): 36000000, ("docker", "compose"): 42000000,
        ("docker", "ps"): 29000000, ("docker", "container"): 31000000, ("docker", "image"): 34000000,

        # Async / Lang
        ("async", "await"): 52000000, ("async", "function"): 48000000, ("await", "fetch"): 32000000,
        ("await", "response"): 29000000, ("api", "endpoint"): 44000000, ("api", "endpoints"): 31000000,
        ("rest", "api"): 46000000, ("graphql", "query"): 28000000, ("database", "table"): 38000000,
        ("database", "query"): 35000000, ("foreign", "key"): 39000000, ("primary", "key"): 48000000,
        ("source", "code"): 62000000, ("open", "source"): 85000000,

        # Business / Email / Daily High-Frequency Connectors
        ("let", "me"): 85000000, ("feel", "free"): 64000000, ("look", "forward"): 78000000,
        ("forward", "to"): 82000000, ("best", "regards"): 45000000, ("attached", "the"): 38000000,
        ("find", "attached"): 52000000, ("please", "find"): 56000000, ("please", "let"): 68000000,
        ("thank", "you"): 145000000, ("as", "well"): 112000000, ("in", "order"): 98000000,
        ("on", "the"): 210000000, ("the", "other"): 88000000, ("by", "the"): 96000000,
        ("as", "soon"): 74000000, ("soon", "as"): 72000000, ("reach", "out"): 48000000,
        ("take", "care"): 62000000, ("good", "morning"): 58000000, ("good", "afternoon"): 42000000,
        ("good", "evening"): 39000000, ("have", "a"): 120000000, ("a", "great"): 74000000
    }

    added_pairs = 0
    for pair, c in new_pairs.items():
        if pair not in pair_dict:
            pair_dict[pair] = c
            added_pairs += 1

    print(f"Added {added_pairs} new bigram pairs.")

    sorted_pairs = sorted(pair_dict.items(), key=lambda kv: kv[1], reverse=True)
    with open(pairs_file, "w", encoding="utf-8") as f:
        for (w1, w2), c in sorted_pairs:
            f.write(f"{w1} {w2} {c}\n")

    # 3. Build Trigram Dataset (english-trigrams.txt)
    # Thousands of high-impact conversational, email, formal, and grammatical trigrams
    trigrams = [
        # Conversational / Email
        ("thank", "you", "so", 58000000),
        ("thank", "you", "very", 62000000),
        ("you", "very", "much", 61000000),
        ("thank", "you", "for", 75000000),
        ("let", "me", "know", 82000000),
        ("me", "know", "if", 68000000),
        ("know", "if", "you", 54000000),
        ("please", "let", "me", 72000000),
        ("please", "feel", "free", 58000000),
        ("feel", "free", "to", 64000000),
        ("free", "to", "contact", 38000000),
        ("free", "to", "reach", 42000000),
        ("to", "reach", "out", 45000000),
        ("look", "forward", "to", 82000000),
        ("forward", "to", "hearing", 48000000),
        ("forward", "to", "seeing", 42000000),
        ("forward", "to", "working", 39000000),
        ("to", "hearing", "from", 46000000),
        ("hearing", "from", "you", 48000000),
        ("hope", "you", "are", 52000000),
        ("you", "are", "doing", 46000000),
        ("are", "doing", "well", 48000000),
        ("hope", "this", "email", 34000000),
        ("this", "email", "finds", 32000000),
        ("email", "finds", "you", 32000000),
        ("finds", "you", "well", 32000000),
        ("please", "find", "attached", 56000000),
        ("find", "attached", "the", 48000000),
        ("attached", "the", "file", 28000000),
        ("attached", "the", "document", 29000000),
        ("as", "per", "our", 34000000),
        ("per", "our", "discussion", 32000000),
        ("as", "per", "your", 36000000),
        ("per", "your", "request", 34000000),
        ("have", "a", "great", 74000000),
        ("a", "great", "day", 68000000),
        ("a", "great", "weekend", 48000000),
        ("have", "a", "good", 82000000),
        ("a", "good", "day", 76000000),
        ("have", "a", "nice", 65000000),
        ("a", "nice", "day", 62000000),
        ("take", "care", "and", 42000000),
        ("all", "the", "best", 48000000),
        ("the", "best", "with", 36000000),
        ("keep", "up", "the", 38000000),
        ("up", "the", "good", 41000000),
        ("the", "good", "work", 44000000),

        # Connectors & Structural Clauses
        ("as", "well", "as", 112000000),
        ("in", "order", "to", 98000000),
        ("at", "the", "same", 78000000),
        ("the", "same", "time", 82000000),
        ("for", "the", "first", 68000000),
        ("the", "first", "time", 72000000),
        ("one", "of", "the", 145000000),
        ("part", "of", "the", 88000000),
        ("as", "a", "result", 74000000),
        ("a", "result", "of", 68000000),
        ("on", "the", "other", 92000000),
        ("the", "other", "hand", 88000000),
        ("in", "terms", "of", 76000000),
        ("as", "soon", "as", 74000000),
        ("soon", "as", "possible", 72000000),
        ("by", "the", "way", 68000000),
        ("in", "addition", "to", 58000000),
        ("in", "front", "of", 54000000),
        ("in", "spite", "of", 42000000),
        ("as", "far", "as", 48000000),
        ("due", "to", "the", 56000000),
        ("according", "to", "the", 62000000),
        ("as", "long", "as", 49000000),
        ("first", "of", "all", 52000000),
        ("in", "case", "of", 46000000),
        ("case", "you", "need", 36000000),
        ("at", "least", "one", 42000000),
        ("at", "the", "end", 58000000),
        ("the", "end", "of", 78000000),
        ("out", "of", "the", 64000000),
        ("from", "time", "to", 42000000),
        ("time", "to", "time", 44000000),
        ("more", "and", "more", 48000000),
        ("over", "and", "over", 38000000),
        ("all", "over", "the", 52000000),
        ("over", "the", "world", 56000000),
        ("rest", "of", "the", 48000000),
        ("middle", "of", "the", 39000000),
        ("top", "of", "the", 44000000),
        ("bottom", "of", "the", 36000000),
        ("beginning", "of", "the", 42000000),
        ("end", "of", "the", 54000000),
        ("most", "of", "the", 68000000),
        ("some", "of", "the", 72000000),
        ("step", "by", "step", 38000000),

        # Pronoun & Verb Sequences
        ("i", "would", "like", 68000000),
        ("would", "like", "to", 82000000),
        ("like", "to", "thank", 42000000),
        ("like", "to", "know", 38000000),
        ("i", "want", "to", 74000000),
        ("want", "to", "know", 42000000),
        ("i", "need", "to", 58000000),
        ("i", "have", "been", 64000000),
        ("have", "been", "working", 38000000),
        ("i", "have", "no", 42000000),
        ("have", "no", "idea", 44000000),
        ("i", "am", "looking", 46000000),
        ("am", "looking", "forward", 48000000),
        ("i", "do", "not", 62000000),
        ("do", "not", "think", 48000000),
        ("do", "not", "know", 56000000),
        ("do", "not", "have", 52000000),
        ("i", "am", "trying", 39000000),
        ("am", "trying", "to", 44000000),
        ("you", "do", "not", 48000000),
        ("do", "not", "need", 42000000),
        ("if", "you", "want", 46000000),
        ("you", "want", "to", 52000000),
        ("if", "you", "have", 68000000),
        ("you", "have", "any", 58000000),
        ("have", "any", "questions", 54000000),
        ("any", "questions", "please", 42000000),
        ("what", "do", "you", 58000000),
        ("do", "you", "think", 52000000),
        ("how", "are", "you", 62000000),
        ("there", "is", "a", 78000000),
        ("there", "is", "no", 64000000),
        ("there", "are", "many", 48000000),
        ("there", "are", "a", 54000000),
        ("are", "a", "lot", 52000000),
        ("a", "lot", "of", 88000000),
        ("it", "is", "a", 84000000),
        ("it", "is", "not", 76000000),
        ("this", "is", "a", 82000000),
        ("it", "would", "be", 68000000),
        ("would", "be", "great", 48000000),
        ("would", "be", "nice", 42000000),
        ("can", "be", "used", 48000000),
        ("be", "used", "to", 52000000),
        ("is", "one", "of", 74000000),
        ("was", "one", "of", 62000000),
        ("is", "said", "to", 38000000),
        ("said", "to", "be", 42000000),
        ("is", "known", "as", 46000000),
        ("known", "as", "the", 52000000),

        # Countries / Proper Compounds
        ("united", "states", "of", 125000000),
        ("states", "of", "america", 120000000),
        ("united", "kingdom", "of", 45000000),
        ("in", "the", "united", 88000000),
        ("of", "the", "united", 82000000),

        # Developer / Tech Sequences
        ("git", "commit", "-m", 48000000),
        ("git", "push", "origin", 46000000),
        ("push", "origin", "main", 42000000),
        ("push", "origin", "master", 38000000),
        ("git", "pull", "origin", 36000000),
        ("git", "checkout", "-b", 34000000),
        ("npm", "run", "build", 38000000),
        ("npm", "run", "dev", 36000000),
        ("npm", "run", "test", 32000000),
        ("npm", "run", "start", 31000000),
        ("docker", "compose", "up", 39000000),
        ("docker", "compose", "down", 34000000),
        ("docker", "compose", "build", 32000000),
        ("pull", "request", "review", 28000000),
        ("create", "pull", "request", 26000000),
        ("async", "await", "function", 29000000),
        ("await", "response", "json", 31000000)
    ]

    # Generate additional combinatorial trigrams from top bigrams
    # e.g., if (A, B) is top bigram and (B, C) is top bigram with high count
    top_second = {}
    for (w1, w2), count in sorted_pairs[:10000]:
        if w1 not in top_second:
            top_second[w1] = []
        if len(top_second[w1]) < 3:
            top_second[w1].append((w2, count))

    # Existing set of trigram tuples
    trigram_set = {(t[0], t[1], t[2]): t[3] for t in trigrams}

    for (w1, w2), c1 in sorted_pairs[:4000]:
        if w2 in top_second:
            for w3, c2 in top_second[w2]:
                if w1 != w2 and w2 != w3 and (w1, w2, w3) not in trigram_set:
                    # geometric mean count
                    combined_count = int(math.isqrt(c1 * c2))
                    if combined_count > 5000000:
                        trigram_set[(w1, w2, w3)] = combined_count

    print(f"Total compiled trigrams: {len(trigram_set)}")

    # Sort and write english-trigrams.txt
    sorted_trigrams = sorted(trigram_set.items(), key=lambda kv: kv[1], reverse=True)
    with open(trigrams_file, "w", encoding="utf-8") as f:
        for (w1, w2, w3), count in sorted_trigrams:
            f.write(f"{w1} {w2} {w3} {count}\n")

    print(f"Saved {len(sorted_trigrams)} trigrams to {trigrams_file}")

    # 4. Pack into binary model english-model.bin
    # Format:
    # Magic: 'K','W','H','M' (0x4D48574B)
    # Version: 1 (uint32)
    # Word count: uint32
    # For each word: UTF-8 string (uint16 byte len + bytes), float32 logFrequency
    # Pair count: uint32
    # For each pair: uint32 firstId, uint32 secondId, float32 logCount, int64 rawCount
    # Trigram count: uint32
    # For each trigram: uint32 firstId, uint32 secondId, uint32 thirdId, float32 logCount

    word_to_id = {}
    for idx, (w, c) in enumerate(sorted_words):
        word_to_id[w] = idx

    print("Packing binary model file...")
    with open(model_bin, "wb") as bf:
        # Magic & Version
        bf.write(struct.pack("<4sI", b"KWHM", 1))

        # Words
        bf.write(struct.pack("<I", len(sorted_words)))
        for w, c in sorted_words:
            w_bytes = w.encode("utf-8")
            log_val = math.log10(max(1, c))
            bf.write(struct.pack("<H", len(w_bytes)))
            bf.write(w_bytes)
            bf.write(struct.pack("<f", log_val))

        # Pairs (filter to words in vocabulary)
        valid_pairs = []
        for (w1, w2), c in sorted_pairs:
            if w1 in word_to_id and w2 in word_to_id:
                id1 = word_to_id[w1]
                id2 = word_to_id[w2]
                log_val = math.log10(max(1, c))
                valid_pairs.append((id1, id2, log_val, c))

        bf.write(struct.pack("<I", len(valid_pairs)))
        for id1, id2, log_val, c in valid_pairs:
            bf.write(struct.pack("<IIfq", id1, id2, log_val, c))

        # Trigrams (filter to words in vocabulary)
        valid_trigrams = []
        for (w1, w2, w3), c in sorted_trigrams:
            if w1 in word_to_id and w2 in word_to_id and w3 in word_to_id:
                id1 = word_to_id[w1]
                id2 = word_to_id[w2]
                id3 = word_to_id[w3]
                log_val = math.log10(max(1, c))
                valid_trigrams.append((id1, id2, id3, log_val))

        bf.write(struct.pack("<I", len(valid_trigrams)))
        for id1, id2, id3, log_val in valid_trigrams:
            bf.write(struct.pack("<IIIf", id1, id2, id3, log_val))

    bin_size_mb = os.path.getsize(model_bin) / (1024 * 1024)
    print(f"Successfully generated {model_bin} ({bin_size_mb:.2f} MB)")
    print(f"Stats: {len(sorted_words)} words, {len(valid_pairs)} pairs, {len(valid_trigrams)} trigrams.")

if __name__ == "__main__":
    main()
