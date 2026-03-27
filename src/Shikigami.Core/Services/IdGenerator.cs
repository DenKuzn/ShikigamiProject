using Shikigami.Core.State;

namespace Shikigami.Core.Services;

/// <summary>
/// Generates unique short alphanumeric IDs for agents and pools.
/// </summary>
public sealed class IdGenerator
{
    private static readonly Random Rng = new();
    private static readonly char[] Chars = "abcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();
    private readonly ShikigamiState _state;

    public IdGenerator(ShikigamiState state)
    {
        _state = state;
    }

    public string NewAgentId(int length = 4)
    {
        while (true)
        {
            var id = RandomString(length);
            if (!_state.Prompts.ContainsKey(id) && !_state.Agents.ContainsKey(id))
                return id;
        }
    }

    public string NewPoolId(int length = 4)
    {
        while (true)
        {
            var id = "pool-" + RandomString(length);
            if (!_state.Pools.ContainsKey(id))
                return id;
        }
    }

    private static string RandomString(int length)
    {
        var buf = new char[length];
        for (var i = 0; i < length; i++)
            buf[i] = Chars[Rng.Next(Chars.Length)];
        return new string(buf);
    }
}
