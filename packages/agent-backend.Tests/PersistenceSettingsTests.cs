using System.IO;
using AgentBackend.Services;
using LocalCodeAgent.Models;
using Microsoft.Extensions.Configuration;

namespace AgentBackend.Tests;

public class PersistenceSettingsTests : IDisposable
{
    private readonly string _tempRoot;

    public PersistenceSettingsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "pm-agent-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public void ResolveDir_WithConfiguredPath_ReturnsThatPathAndCreatesDirectory()
    {
        var expectedDir = Path.Combine(_tempRoot, "custom-sessions");
        var result = SessionPersistence.ResolveDir(expectedDir);

        Assert.Equal(expectedDir, result);
        Assert.True(Directory.Exists(expectedDir));
    }

    [Fact]
    public void ResolveDir_WithEmptyPath_UsesBaseDirectorySessions()
    {
        var result = SessionPersistence.ResolveDir("");
        var expected = Path.Combine(AppContext.BaseDirectory, "sessions");

        Assert.Equal(expected, result);
        Assert.True(Directory.Exists(result));
    }

    [Fact]
    public void ResolveDir_WithNullPath_UsesBaseDirectorySessions()
    {
        var result = SessionPersistence.ResolveDir(null);
        var expected = Path.Combine(AppContext.BaseDirectory, "sessions");

        Assert.Equal(expected, result);
        Assert.True(Directory.Exists(result));
    }

    [Fact]
    public void ResolveDir_WithRelativePath_AnchorsToBaseDirectory()
    {
        var result = SessionPersistence.ResolveDir("relative-sessions");
        var expected = Path.Combine(AppContext.BaseDirectory, "relative-sessions");

        Assert.Equal(expected, result);
        Assert.True(Directory.Exists(result));
    }

    [Fact]
    public void Bind_FromConfiguration_SetsSessionsDir()
    {
        var configDir = Path.Combine(_tempRoot, "config-sessions");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PersistenceSettings:SessionsDir"] = configDir
            })
            .Build();

        var config = new AgentConfig();
        configuration.GetSection("PersistenceSettings").Bind(config.PersistenceSettings);

        Assert.Equal(configDir, config.PersistenceSettings.SessionsDir);
    }

    [Fact]
    public void Bind_WithEmptySessionsDir_ReturnsEmptyString()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PersistenceSettings:SessionsDir"] = ""
            })
            .Build();

        var config = new AgentConfig();
        configuration.GetSection("PersistenceSettings").Bind(config.PersistenceSettings);

        Assert.Equal("", config.PersistenceSettings.SessionsDir);
    }

    [Fact]
    public void Bind_WithEnvironmentVariableOverride_RespectsOverride()
    {
        var envDir = Path.Combine(_tempRoot, "env-sessions");
        var appSettingsDir = Path.Combine(_tempRoot, "appsettings-sessions");

        // Simula: appsettings.json ha un valore, ma la variabile d'ambiente lo override.
        // In ASP.NET Core le env var hanno priorità maggiore di appsettings.json.
        using var envVar = SetEnvironmentVariable("PersistenceSettings__SessionsDir", envDir);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PersistenceSettings:SessionsDir"] = appSettingsDir
            })
            .AddEnvironmentVariables()
            .Build();

        var config = new AgentConfig();
        configuration.GetSection("PersistenceSettings").Bind(config.PersistenceSettings);

        Assert.Equal(envDir, config.PersistenceSettings.SessionsDir);
    }

    private static IDisposable SetEnvironmentVariable(string name, string? value)
    {
        var oldValue = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        return new EnvVarCleanup(name, oldValue);
    }

    private sealed class EnvVarCleanup : IDisposable
    {
        private readonly string _name;
        private readonly string? _oldValue;

        public EnvVarCleanup(string name, string? oldValue)
        {
            _name = name;
            _oldValue = oldValue;
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _oldValue);
        }
    }
}
