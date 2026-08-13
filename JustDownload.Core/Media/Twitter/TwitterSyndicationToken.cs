using System.Globalization;

namespace JustDownload.Core.Media.Twitter;

/// <summary>
/// Computes the deterministic syndication token Twitter's public embed API expects — the same value
/// Vercel's MIT <c>react-tweet</c> <c>getToken</c> and yt-dlp <c>_generate_syndication_token</c> produce.
/// A random string used to "work"; since yt-dlp PR #12107 (Feb 2025) the real formula is required.
/// <para>
/// Formula: take the tweet id, scale it by <c>1e-15</c>, multiply by <c>π</c>, render that double as a
/// base-36 string <em>including the fractional part</em> (mirroring JS <c>(n).toString(36)</c>), then strip
/// every <c>'0'</c> and <c>'.'</c>. The base-36 rendering must reproduce V8's <c>DoubleToRadixString</c>
/// exactly — including its bounded fractional precision and "round-half-up with carry" rule — or the server
/// rejects the token. <see cref="Generate"/> is exposed (internal, visible to tests) so the value can be
/// cross-checked against a reference JS/Node computation for any given tweet id.
/// </para>
/// </summary>
internal static class TwitterSyndicationToken
{
    private const int Radix = 36;
    private const string Chars = "0123456789abcdefghijklmnopqrstuvwxyz";

    /// <summary>
    /// Returns the token for <paramref name="tweetId"/>, or <see cref="string.Empty"/> if it cannot be
    /// parsed as a number.
    /// </summary>
    public static string Generate(string? tweetId)
    {
        if (string.IsNullOrWhiteSpace(tweetId))
        {
            return string.Empty;
        }

        double value = double.Parse(tweetId, CultureInfo.InvariantCulture);
        double scaled = (value / 1e15) * Math.PI;
        string raw = DoubleToRadixString(scaled);

        // Strip '0' and '.' exactly like JS (number).toString(36).replace(/0|\./g, "")
        var cleaned = new System.Text.StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            if (c == '0' || c == '.')
            {
                continue;
            }

            cleaned.Append(c);
        }

        return cleaned.ToString();
    }

    /// <summary>
    /// Reproduces V8's <c>Number.prototype.toString(radix)</c> for a double — the integer part in base-36
    /// with a bounded-precision fractional part (stopping when the remaining fraction is below half a ULP,
    /// with half-to-even carry). This is what makes the token match the server's expectation.
    /// </summary>
    private static string DoubleToRadixString(double value)
    {
        bool negative = value < 0;
        if (negative)
        {
            value = -value;
        }

        double integer = Math.Floor(value);
        double fraction = value - integer;
        double delta = 0.5 * (NextDouble(value) - value);
        double smallest = BitConverter.Int64BitsToDouble(1); // smallest positive subnormal (2^-1074)
        if (delta < smallest)
        {
            delta = smallest;
        }

        var fracBuilder = new System.Text.StringBuilder();
        if (fraction > delta)
        {
            do
            {
                fraction *= Radix;
                delta *= Radix;
                int digit = (int)Math.Floor(fraction);
                fracBuilder.Append(Chars[digit]);
                fraction -= digit;

                // V8 rounds the last digit when the remainder is >= 0.5 (half-up), then backtraces a carry.
                if (fraction > 0.5 || (fraction == 0.5 && (digit & 1) != 0))
                {
                    if (fraction + delta > 1)
                    {
                        bool carry = true;
                        for (int k = fracBuilder.Length - 1; k >= 0 && carry; k--)
                        {
                            char c = fracBuilder[k];
                            int d = c >= 'a' ? (c - 'a' + 10) : (c - '0');
                            if (d + 1 < Radix)
                            {
                                fracBuilder[k] = Chars[d + 1];
                                carry = false;
                            }
                            else
                            {
                                fracBuilder[k] = '0';
                            }
                        }

                        if (carry)
                        {
                            integer += 1;
                        }

                        break;
                    }
                }
            }
            while (fraction > delta);
        }

        var intBuilder = new System.Text.StringBuilder();
        if (integer == 0)
        {
            intBuilder.Append('0');
        }
        else
        {
            long i = (long)integer;
            while (i > 0)
            {
                int remainder = (int)(i % Radix);
                intBuilder.Insert(0, Chars[remainder]);
                i = (i - remainder) / Radix;
            }
        }

        return (negative ? "-" : string.Empty) + intBuilder +
               (fracBuilder.Length > 0 ? "." + fracBuilder : string.Empty);
    }

    /// <summary>
    /// The next representable <see cref="double"/> strictly greater than <paramref name="value"/> (mirrors
    /// V8's <c>Double::NextDouble</c>): increment the bit pattern for positive values, decrement for
    /// negative, and return the smallest subnormal for +0.
    /// </summary>
    private static double NextDouble(double value)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        if (bits == 0)
        {
            return BitConverter.Int64BitsToDouble(1);
        }

        if (bits < 0)
        {
            return BitConverter.Int64BitsToDouble(bits - 1);
        }

        return BitConverter.Int64BitsToDouble(bits + 1);
    }
}
