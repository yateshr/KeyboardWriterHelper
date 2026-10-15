using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KeyboardWriterHelper

{

    // ---- The text files compiled into the exe (data\*.txt and the two count lists) ----

    // Blank lines and lines starting with # are skipped.

    

    static class Data

    {

        public static StreamReader Open(string name)

        {

            Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);

            if (stream == null)

            {

                foreach (string res in Assembly.GetExecutingAssembly().GetManifestResourceNames())

                {

                    if (res.Equals(name, StringComparison.OrdinalIgnoreCase) || res.EndsWith("." + name, StringComparison.OrdinalIgnoreCase))

                    {

                        stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(res);

                        break;

                    }

                }

            }

            if (stream == null) throw new InvalidOperationException("\"" + name + "\" is missing from this build.");

            return new StreamReader(stream, Encoding.UTF8);

        }

    

        public static List<string> Lines(string name)

        {

            List<string> lines = new List<string>();

            using (StreamReader reader = Open(name))

            {

                string line;

                while ((line = reader.ReadLine()) != null)

                {

                    line = line.Trim();

                    if (line.Length > 0 && line[0] != '#') lines.Add(line);

                }

            }

            return lines;

        }

    

        // Any number of words per line.

        public static HashSet<string> Words(string name)

        {

            HashSet<string> words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string line in Lines(name))

                foreach (string word in line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))

                    words.Add(word);

            return words;

        }

    

        // "key=value" lines.

        public static Dictionary<string, string> Pairs(string name)

        {

            Dictionary<string, string> pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string line in Lines(name))

            {

                int eq = line.IndexOf('=');

                if (eq > 0 && eq < line.Length - 1) pairs[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();

            }

            return pairs;

        }

    }

}

