using System;
using System.Collections.Generic;

namespace System
{
    // Simple helpers so code like args.GetInt("-port", 7777) compiles.
    public static class CliArgsExtensions
    {
        public static int GetInt(this IList<string> args, string key, int defaultValue)
        {
            if (args == null) return defaultValue;
            for (int i = 0; i < args.Count - 1; i++)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(args[i + 1], out var v))
                {
                    return v;
                }
            }
            return defaultValue;
        }

        public static ushort GetUShort(this IList<string> args, string key, ushort defaultValue)
        {
            if (args == null) return defaultValue;
            for (int i = 0; i < args.Count - 1; i++)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase) &&
                    ushort.TryParse(args[i + 1], out var v))
                {
                    return v;
                }
            }
            return defaultValue;
        }

        public static string Get(this IList<string> args, string key, string defaultValue)
        {
            if (args == null) return defaultValue;
            for (int i = 0; i < args.Count - 1; i++)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }
            return defaultValue;
        }
    }
}
// Brief dev comment: Placed in namespace System so most files already "using System;" pick up the extension automatically.