namespace Practical.HttpClient;

public sealed class SimpleHttpClientOptions
{
    private int _maxAutomaticRedirections = 10;

    public bool AllowAutoRedirect { get; init; } = true;

    public int MaxAutomaticRedirections
    {
        get => _maxAutomaticRedirections;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _maxAutomaticRedirections = value;
        }
    }
}
