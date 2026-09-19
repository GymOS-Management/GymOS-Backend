using GymOS.Platform.Application.Settings.Abstractions;

namespace GymOS.TestingSupport;

public sealed class FakeSettings<T> : ISettings<T> where T : class, new()
{
    public FakeSettings(T current) => Current = current;

    public T Current { get; set; }
}
