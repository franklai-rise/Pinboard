using System.Text;

namespace Pinboard.App.Compatibility
{
    /// <summary>
    /// Minimal LZ-String Base64 codec used for Obsidian Excalidraw compatibility.
    /// The algorithm is derived from lz-string 1.4.5 by Pieroxy (MIT).
    /// </summary>
    public static class LzStringCodec
    {
        private const string Base64Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=";

        public static string CompressToBase64(string? input)
        {
            if (input is null)
            {
                return string.Empty;
            }

            var compressed = Compress(input, 6, value => Base64Alphabet[value]);
            return (compressed.Length % 4) switch
            {
                0 => compressed,
                1 => compressed + "===",
                2 => compressed + "==",
                3 => compressed + "=",
                _ => compressed,
            };
        }

        public static string? DecompressFromBase64(string? input)
        {
            if (input is null)
            {
                return string.Empty;
            }

            if (input.Length == 0)
            {
                return null;
            }

            var reverseAlphabet = new int[128];
            Array.Fill(reverseAlphabet, -1);
            for (var index = 0; index < Base64Alphabet.Length; index++)
            {
                reverseAlphabet[Base64Alphabet[index]] = index;
            }

            foreach (var character in input)
            {
                if (character >= reverseAlphabet.Length || reverseAlphabet[character] < 0)
                {
                    return null;
                }
            }

            return Decompress(
                input.Length,
                32,
                index => index < input.Length ? reverseAlphabet[input[index]] : 0);
        }

        private static string Compress(string uncompressed, int bitsPerCharacter, Func<int, char> getCharacter)
        {
            var dictionary = new Dictionary<string, int>(StringComparer.Ordinal);
            var dictionaryToCreate = new HashSet<string>(StringComparer.Ordinal);
            var output = new StringBuilder();
            var word = string.Empty;
            var enlargeIn = 2;
            var dictionarySize = 3;
            var numberOfBits = 2;
            var outputValue = 0;
            var outputPosition = 0;

            void WriteBit(int bit)
            {
                outputValue = (outputValue << 1) | bit;
                if (outputPosition == bitsPerCharacter - 1)
                {
                    output.Append(getCharacter(outputValue));
                    outputPosition = 0;
                    outputValue = 0;
                }
                else
                {
                    outputPosition++;
                }
            }

            void WriteValue(int value, int bitCount)
            {
                for (var index = 0; index < bitCount; index++)
                {
                    WriteBit(value & 1);
                    value >>= 1;
                }
            }

            void UpdateBitWidth()
            {
                enlargeIn--;
                if (enlargeIn == 0)
                {
                    enlargeIn = 1 << numberOfBits;
                    numberOfBits++;
                }
            }

            void WriteWord(string value)
            {
                if (dictionaryToCreate.Contains(value))
                {
                    var character = value[0];
                    if (character < 256)
                    {
                        WriteValue(0, numberOfBits);
                        WriteValue(character, 8);
                    }
                    else
                    {
                        WriteValue(1, numberOfBits);
                        WriteValue(character, 16);
                    }

                    UpdateBitWidth();
                    dictionaryToCreate.Remove(value);
                }
                else
                {
                    WriteValue(dictionary[value], numberOfBits);
                }

                UpdateBitWidth();
            }

            foreach (var character in uncompressed)
            {
                var current = character.ToString();
                if (!dictionary.ContainsKey(current))
                {
                    dictionary[current] = dictionarySize++;
                    dictionaryToCreate.Add(current);
                }

                var combined = word + current;
                if (dictionary.ContainsKey(combined))
                {
                    word = combined;
                    continue;
                }

                WriteWord(word);
                dictionary[combined] = dictionarySize++;
                word = current;
            }

            if (word.Length > 0)
            {
                WriteWord(word);
            }

            WriteValue(2, numberOfBits);

            while (true)
            {
                outputValue <<= 1;
                if (outputPosition == bitsPerCharacter - 1)
                {
                    output.Append(getCharacter(outputValue));
                    break;
                }

                outputPosition++;
            }

            return output.ToString();
        }

        private static string? Decompress(int length, int resetValue, Func<int, int> getNextValue)
        {
            var dictionary = new List<string?> { null, null, null };
            var result = new StringBuilder();
            var enlargeIn = 4;
            var dictionarySize = 4;
            var numberOfBits = 3;
            var data = new BitReader(getNextValue(0), resetValue, 1, resetValue, getNextValue, length);

            var next = data.ReadBits(2);
            string current;
            switch (next)
            {
                case 0:
                    current = ((char)data.ReadBits(8)).ToString();
                    break;
                case 1:
                    current = ((char)data.ReadBits(16)).ToString();
                    break;
                case 2:
                    return string.Empty;
                default:
                    return null;
            }

            dictionary.Add(current);
            var word = current;
            result.Append(current);

            while (true)
            {
                if (data.Index > length)
                {
                    return string.Empty;
                }

                var code = data.ReadBits(numberOfBits);
                switch (code)
                {
                    case 0:
                        dictionary.Add(((char)data.ReadBits(8)).ToString());
                        code = dictionarySize++;
                        enlargeIn--;
                        break;
                    case 1:
                        dictionary.Add(((char)data.ReadBits(16)).ToString());
                        code = dictionarySize++;
                        enlargeIn--;
                        break;
                    case 2:
                        return result.ToString();
                }

                if (enlargeIn == 0)
                {
                    enlargeIn = 1 << numberOfBits;
                    numberOfBits++;
                }

                string entry;
                if (code < dictionary.Count && dictionary[code] is { } dictionaryEntry)
                {
                    entry = dictionaryEntry;
                }
                else if (code == dictionarySize)
                {
                    entry = word + word[0];
                }
                else
                {
                    return null;
                }

                result.Append(entry);
                dictionary.Add(word + entry[0]);
                dictionarySize++;
                enlargeIn--;
                word = entry;

                if (enlargeIn == 0)
                {
                    enlargeIn = 1 << numberOfBits;
                    numberOfBits++;
                }
            }
        }

        private sealed class BitReader(
            int value,
            int position,
            int index,
            int resetValue,
            Func<int, int> getNextValue,
            int length)
        {
            private int _value = value;
            private int _position = position;

            public int Index { get; private set; } = index;

            public int ReadBits(int bitCount)
            {
                var bits = 0;
                var maximumPower = 1 << bitCount;
                var power = 1;
                while (power != maximumPower)
                {
                    var bit = _value & _position;
                    _position >>= 1;
                    if (_position == 0)
                    {
                        _position = resetValue;
                        _value = Index < length ? getNextValue(Index) : 0;
                        Index++;
                    }

                    if (bit > 0)
                    {
                        bits |= power;
                    }

                    power <<= 1;
                }

                return bits;
            }
        }
    }
}

// Source-compatible façade for existing callers while the vulnerable NuGet package is removed.
namespace LZStringCSharp
{
    public static class LZString
    {
        public static string CompressToBase64(string? input) =>
            Pinboard.App.Compatibility.LzStringCodec.CompressToBase64(input);

        public static string? DecompressFromBase64(string? input) =>
            Pinboard.App.Compatibility.LzStringCodec.DecompressFromBase64(input);
    }
}
