namespace ibsCompiler
{
    /// <summary>
    /// Centralized parsing for the headless CLI flags added to interactive
    /// commands (set_options, set_actions, set_required_fields,
    /// set_table_locations, set_profile).
    ///
    /// Conventions:
    ///   * Long flags use --name (case-insensitive match).
    ///   * Option values are accepted as either "--name value" or "--name=value".
    ///   * Repeatable flags (e.g. --add, --customize, --alias) collect all
    ///     occurrences in order.
    ///   * Helpers consume (remove) the matched tokens from the args list so
    ///     downstream parsers don't re-process them.
    /// </summary>
    public static class CliArgs
    {
        public static bool HasFlag(List<string> args, params string[] names)
        {
            for (int i = 0; i < args.Count; i++)
            {
                if (Matches(args[i], names))
                {
                    args.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        public static string? GetOption(List<string> args, params string[] names)
        {
            for (int i = 0; i < args.Count; i++)
            {
                var (key, inlineValue) = SplitKeyValue(args[i]);
                if (!Matches(key, names)) continue;

                if (inlineValue != null)
                {
                    args.RemoveAt(i);
                    return inlineValue;
                }
                if (i + 1 < args.Count)
                {
                    var value = args[i + 1];
                    args.RemoveAt(i + 1);
                    args.RemoveAt(i);
                    return value;
                }
                args.RemoveAt(i);
                return "";
            }
            return null;
        }

        public static List<string> GetMulti(List<string> args, params string[] names)
        {
            var values = new List<string>();
            int i = 0;
            while (i < args.Count)
            {
                var (key, inlineValue) = SplitKeyValue(args[i]);
                if (!Matches(key, names)) { i++; continue; }

                if (inlineValue != null)
                {
                    values.Add(inlineValue);
                    args.RemoveAt(i);
                    continue;
                }
                if (i + 1 < args.Count)
                {
                    values.Add(args[i + 1]);
                    args.RemoveAt(i + 1);
                    args.RemoveAt(i);
                    continue;
                }
                values.Add("");
                args.RemoveAt(i);
            }
            return values;
        }

        /// <summary>True if any of the given flag names appear anywhere in args
        /// (does NOT consume). Used to detect "is the caller asking for headless
        /// mode at all?" before routing into the menu.</summary>
        public static bool AnyPresent(List<string> args, params string[] names)
        {
            foreach (var arg in args)
            {
                var (key, _) = SplitKeyValue(arg);
                if (Matches(key, names)) return true;
            }
            return false;
        }

        public static bool IsInteractiveTty()
            => !Console.IsInputRedirected && !Console.IsOutputRedirected;

        /// <summary>
        /// Resolves a tri-state boolean from CLI flags: returns true if any of
        /// <paramref name="trueFlags"/> is present, false if any of
        /// <paramref name="falseFlags"/> is present, null if neither is. Used to
        /// override Y/N prompts: <c>cli ?? ConsoleYesNo(...)</c>.
        ///
        /// Non-consuming on purpose — a single shared flag (e.g. --skip-edit) can
        /// appear in the falseFlags list of multiple resolutions, and consuming it
        /// on the first call would silently break the second.
        /// </summary>
        public static bool? ResolveBool(List<string> args, string[] trueFlags, string[] falseFlags)
        {
            if (AnyPresent(args, trueFlags)) return true;
            if (AnyPresent(args, falseFlags)) return false;
            return null;
        }

        /// <summary>
        /// Strips every "--*"-prefixed token from the argument list. For each long
        /// flag, the following token is treated as the flag's value and also removed
        /// UNLESS the flag name appears in <paramref name="boolFlagNames"/> (in which
        /// case it stands alone). Use this in Program.cs entry points BEFORE calling
        /// the legacy compile_variables() so the headless flags don't interfere with
        /// the positional server-name fallback.
        /// </summary>
        public static void StripLongFlags(List<string> args, IEnumerable<string> boolFlagNames)
        {
            var bools = new HashSet<string>(boolFlagNames, StringComparer.OrdinalIgnoreCase);
            int i = 0;
            while (i < args.Count)
            {
                var arg = args[i];
                if (!arg.StartsWith("--", StringComparison.Ordinal)) { i++; continue; }

                bool inlineEq = arg.Contains('=');
                string key = inlineEq ? arg.Substring(0, arg.IndexOf('=')) : arg;
                args.RemoveAt(i);
                if (!inlineEq && !bools.Contains(key) && i < args.Count
                    && !args[i].StartsWith("-", StringComparison.Ordinal))
                {
                    args.RemoveAt(i);
                }
                // do not advance i — we removed at least one token here
            }
        }

        private static bool Matches(string token, string[] names)
        {
            foreach (var name in names)
            {
                if (string.Equals(token, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static (string key, string? value) SplitKeyValue(string token)
        {
            if (token.StartsWith("--"))
            {
                int eq = token.IndexOf('=');
                if (eq > 0) return (token.Substring(0, eq), token.Substring(eq + 1));
            }
            return (token, null);
        }
    }
}
