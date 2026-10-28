using System.Text;

namespace PcChecker.Core.Games;

/// <summary>
/// One node of a Valve KeyValues ("VDF") text file: either a value or a block of child nodes. Keys are matched without
/// regard to case, as Steam itself does; when a key repeats in a block, the first one wins for lookups and every one is
/// kept in <see cref="Children"/>.
/// </summary>
public sealed class VdfNode
{
    private readonly List<KeyValuePair<string, VdfNode>> _children = [];
    private readonly Dictionary<string, VdfNode> _index = new(StringComparer.OrdinalIgnoreCase);

    public VdfNode(string? value = null) => Value = value;

    /// <summary>The text value, or null for a block.</summary>
    public string? Value { get; }

    public bool IsBlock => Value is null;

    /// <summary>The children in file order.</summary>
    public IReadOnlyList<KeyValuePair<string, VdfNode>> Children => _children;

    /// <summary>The child named <paramref name="key"/> (any case), or null.</summary>
    public VdfNode? this[string key] => _index.GetValueOrDefault(key);

    /// <summary>The value of the child named <paramref name="key"/>, or null when it is missing or a block.</summary>
    public string? Text(string key) => this[key]?.Value;

    /// <summary>Follows a path of keys (any case), or returns null as soon as one is missing.</summary>
    public VdfNode? Path(params string[] keys)
    {
        VdfNode? node = this;
        foreach (var key in keys)
        {
            node = node?[key];
        }

        return node;
    }

    internal void Add(string key, VdfNode node)
    {
        _children.Add(new(key, node));
        _index.TryAdd(key, node);
    }
}

/// <summary>
/// A small, forgiving reader for Valve's KeyValues text format (libraryfolders.vdf, appmanifest_*.acf,
/// localconfig.vdf). Handles quoted and bare tokens, the escapes Steam writes (\" \\ \n \t), // comments, platform
/// conditions such as [$WIN32] (ignored) and #include/#base lines (skipped). A damaged file gives back whatever was
/// read before the damage instead of throwing.
/// </summary>
public static class VdfParser
{
    /// <summary>Parses <paramref name="text"/> into a root block whose children are the file's top-level keys.</summary>
    public static VdfNode Parse(string? text)
    {
        var root = new VdfNode();
        if (string.IsNullOrEmpty(text))
        {
            return root;
        }

        var reader = new Reader(text);
        ReadBlock(reader, root, depth: 0);
        return root;
    }

    private static void ReadBlock(Reader reader, VdfNode block, int depth)
    {
        while (true)
        {
            var key = reader.Next();
            if (key is null || key.Value.Kind == TokenKind.Close)
            {
                return;
            }

            if (key.Value.Kind == TokenKind.Open)
            {
                // A block with no key: read it so the rest of the file stays in step, then drop it.
                ReadBlock(reader, new VdfNode(), depth + 1);
                continue;
            }

            if (key.Value.Kind is TokenKind.Directive or TokenKind.Condition)
            {
                continue;
            }

            var next = reader.Next();
            while (next is { Kind: TokenKind.Condition })
            {
                next = reader.Next();
            }

            if (next is null)
            {
                return;
            }

            switch (next.Value.Kind)
            {
                case TokenKind.Open:
                    var child = new VdfNode();
                    if (depth < 64)
                    {
                        ReadBlock(reader, child, depth + 1);
                    }

                    block.Add(key.Value.Text, child);
                    break;
                case TokenKind.Text:
                    block.Add(key.Value.Text, new VdfNode(next.Value.Text));
                    reader.SkipCondition();
                    break;
                case TokenKind.Close:
                    // A key with nothing after it at the end of a block.
                    return;
                default:
                    break;
            }
        }
    }

    private enum TokenKind
    {
        Text,
        Open,
        Close,
        Condition,
        Directive,
    }

    private readonly record struct Token(TokenKind Kind, string Text);

    private sealed class Reader(string text)
    {
        private int _pos;

        public Token? Next()
        {
            SkipSpaceAndComments();
            if (_pos >= text.Length)
            {
                return null;
            }

            char c = text[_pos];
            switch (c)
            {
                case '{':
                    _pos++;
                    return new Token(TokenKind.Open, "{");
                case '}':
                    _pos++;
                    return new Token(TokenKind.Close, "}");
                case '"':
                    return new Token(TokenKind.Text, ReadQuoted());
                case '[':
                    int end = text.IndexOf(']', _pos);
                    end = end < 0 ? text.Length : end + 1;
                    var condition = text[_pos..end];
                    _pos = end;
                    return new Token(TokenKind.Condition, condition);
                case '#':
                    // #include / #base "file": the rest of the line is not data.
                    int lineEnd = text.IndexOf('\n', _pos);
                    _pos = lineEnd < 0 ? text.Length : lineEnd + 1;
                    return new Token(TokenKind.Directive, string.Empty);
                default:
                    return new Token(TokenKind.Text, ReadBare());
            }
        }

        /// <summary>Skips a [$CONDITION] that follows a value on the same line.</summary>
        public void SkipCondition()
        {
            int save = _pos;
            while (_pos < text.Length && (text[_pos] == ' ' || text[_pos] == '\t'))
            {
                _pos++;
            }

            if (_pos < text.Length && text[_pos] == '[')
            {
                int end = text.IndexOf(']', _pos);
                _pos = end < 0 ? text.Length : end + 1;
                return;
            }

            _pos = save;
        }

        private void SkipSpaceAndComments()
        {
            while (_pos < text.Length)
            {
                char c = text[_pos];
                if (char.IsWhiteSpace(c) || c == '﻿')
                {
                    _pos++;
                }
                else if (c == '/' && _pos + 1 < text.Length && text[_pos + 1] == '/')
                {
                    int lineEnd = text.IndexOf('\n', _pos);
                    _pos = lineEnd < 0 ? text.Length : lineEnd + 1;
                }
                else
                {
                    return;
                }
            }
        }

        private string ReadQuoted()
        {
            _pos++; // opening quote
            var sb = new StringBuilder();
            while (_pos < text.Length)
            {
                char c = text[_pos++];
                if (c == '"')
                {
                    return sb.ToString();
                }

                if (c == '\\' && _pos < text.Length)
                {
                    char e = text[_pos++];
                    sb.Append(e switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        '"' => '"',
                        '\\' => '\\',
                        _ => e,
                    });
                    if (e is not ('n' or 't' or '"' or '\\'))
                    {
                        // Unknown escape: keep the backslash too, so Windows paths written without doubling survive.
                        sb.Insert(sb.Length - 1, '\\');
                    }

                    continue;
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        private string ReadBare()
        {
            int start = _pos;
            while (_pos < text.Length && !char.IsWhiteSpace(text[_pos]) && text[_pos] is not ('{' or '}' or '"'))
            {
                _pos++;
            }

            if (_pos == start)
            {
                // A stray character that cannot start anything: step over it.
                _pos++;
                return text[start..Math.Min(_pos, text.Length)];
            }

            return text[start.._pos];
        }
    }
}
