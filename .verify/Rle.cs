using System;
using System.Collections.Generic;
using System.Text;

namespace ConwayGameOfLife.Verify
{
    /// <summary>
    /// Minimal RLE decoder for Conway patterns. Used to derive canonical cell lists
    /// instead of transcribing coordinates by hand.
    /// </summary>
    internal static class Rle
    {
        public static List<(int X, int Y)> Decode(string rle)
        {
            var cells = new List<(int X, int Y)>();
            bool headerSeen = false;
            int x = 0;
            int y = 0;
            int run = 0;

            foreach (string rawLine in rle.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = rawLine.Trim();

                // Comments and blank lines never contribute pattern data.
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                // The 'x = w, y = h, rule = ...' line is the last header line.
                if (!headerSeen && line[0] == 'x')
                {
                    headerSeen = true;
                    continue;
                }

                bool terminated = false;
                foreach (char c in line)
                {
                    if (c == ' ')
                    {
                        continue;
                    }

                    if (c >= '0' && c <= '9')
                    {
                        run = run * 10 + (c - '0');
                        continue;
                    }

                    int count = run == 0 ? 1 : run;
                    run = 0;

                    switch (c)
                    {
                        case 'b':
                            x += count;
                            break;

                        case 'o':
                            for (int i = 0; i < count; i++, x++)
                            {
                                cells.Add((x, y));
                            }

                            break;

                        case '$':
                            y += count;
                            x = 0;
                            break;

                        case '!':
                            terminated = true;
                            break;

                        default:
                            throw new InvalidOperationException($"Unexpected RLE token '{c}'.");
                    }
                }

                if (terminated)
                {
                    break;
                }
            }

            return cells;
        }

        /// <summary>Compact C# initialiser for a cell list, wrapped for readability.</summary>
        public static string ToCSharp(IReadOnlyList<(int X, int Y)> cells, int indent)
        {
            string pad = new string(' ', indent);
            var sb = new StringBuilder();
            for (int i = 0; i < cells.Count; i++)
            {
                string token = $"new({cells[i].X}, {cells[i].Y})";
                sb.Append(token);
                if (i < cells.Count - 1)
                {
                    sb.Append(", ");
                }

                int lastBreak = sb.ToString().LastIndexOf('\n');
                if (sb.Length - lastBreak > 100 && i < cells.Count - 1)
                {
                    sb.Append('\n').Append(pad);
                }
            }

            return sb.ToString();
        }
    }
}
