using Xunit;

namespace SteamVRTranslator.App.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MainWindowTestCollection
{
    public const string Name = "MainWindow UI";
}
