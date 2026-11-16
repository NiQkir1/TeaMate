using TeaMate.Core.Configuration;
using Xunit;

namespace TeaMate.Tests;

public class TeaMateConfigTests
{
    [Fact]
    public void Config_Loads_Successfully_From_Yaml_File()
    {
        // Ищем config.yaml относительно тестового запуска
        var config = TeaMateConfigLoader.Load();

        Assert.NotNull(config);
        Assert.Equal("1.0", config.Version);

        // 1. Проверка LLM параметров
        Assert.True(config.Llm.Provider is "nvidia" or "gemini");
        Assert.Equal("nvidia/nemotron-3-ultra-550b-a55b", config.Llm.Models.ChatModel);
        Assert.Equal("nvidia/nemotron-3.5-bolt-e4b", config.Llm.Models.FastModel);
        Assert.Equal("nvidia/nemotron-3.5-bolt-e4b", config.Llm.Models.FallbackModel);
        Assert.NotEqual(config.Llm.Models.ChatModel, config.Llm.Models.FallbackModel);
        Assert.Equal("gemini-embedding-2", config.Llm.Models.EmbeddingModel);

        // 2. Проверка гиперпараметров (температура, токены)
        Assert.Equal(0.2, config.Llm.Parameters.Temperature);
        Assert.Equal(4096, config.Llm.Parameters.MaxTokens);
        Assert.Equal(0.95, config.Llm.Parameters.TopP);

        // 3. Проверка Rate Limits (расширенный RPM для автономного вызова инструментов)
        Assert.True(config.Llm.RateLimits.Rpm is >= 40 and <= 60);
        Assert.Equal(1000000, config.Llm.RateLimits.Tpm);
        Assert.Equal(5, config.Llm.RateLimits.MaxConcurrentRequests);

        // 4. Проверка устойчивости и 3 повторов
        Assert.Equal(3, config.Resilience.MaxRetries);
        Assert.True(config.Resilience.AutoRollbackOnFailure);

        // 5. Проверка RAG
        Assert.Equal(1536, config.Rag.EmbeddingDimensions);
        Assert.Equal(5, config.Rag.TopK);
        Assert.Equal(800, config.Rag.Chunking.ChunkSize);

        // 6. Проверка параметров защиты периметра и изоляции MCP
        Assert.True(config.Security.EnforceInternalServiceAuth);
        Assert.True(config.Security.AntiPortScanEnabled);
        Assert.True(config.Security.BlockKnownScannerUserAgents);
        Assert.NotEmpty(config.Security.ScannerProbePathPatterns);

        // 7. Проверка конфигурации автономного агента
        Assert.Equal("autonomous_tool_calling", config.Agent.Mode);

        // 8. Проверка параметров распознавания речи и аудиопротоколирования (Audio)
        Assert.NotNull(config.Audio);
        Assert.True(config.Audio.Enabled);
        Assert.Equal("auto", config.Audio.Provider);
        Assert.Equal("config/vocabulary.yaml", config.Audio.VocabularyPath);
        Assert.Equal(1800, config.Audio.Engine.TimeoutSeconds);
        Assert.Equal("gemini-2.5-flash", config.Audio.Gemini.Model);
        Assert.Equal(100, config.Audio.MaxFileSizeMb);

        // 9. Проверка параметров MCP URLs и Календаря (Calendar & McpUrls)
        Assert.NotNull(config.McpUrls);
        Assert.Equal("http://mcp-calendar:8084", config.McpUrls.Calendar);
        Assert.NotNull(config.Calendar);
        Assert.True(config.Calendar.Enabled);
        Assert.Equal("http://mcp-calendar:8084", config.Calendar.ServiceUrl);
        Assert.Equal("Europe/Moscow", config.Calendar.Timezone);
        Assert.Equal("09:00", config.Calendar.WorkingHoursStart);
        Assert.Equal("18:00", config.Calendar.WorkingHoursEnd);
        Assert.Equal(10, config.Calendar.DefaultBufferMinutes);
        Assert.Equal(30, config.Calendar.DefaultSlotDurationMinutes);
    }

    [Fact]
    public void Config_Fallback_To_Defaults_If_File_Not_Found()
    {
        var config = TeaMateConfigLoader.Load("non_existing_file_9999.yaml");

        Assert.NotNull(config);
        Assert.Equal("gemini", config.Llm.Provider);
        Assert.Equal("gemini-1.5-flash", config.Llm.Models.ChatModel);
        Assert.Equal(60, config.Llm.RateLimits.Rpm);
        Assert.Equal(0.2, config.Llm.Parameters.Temperature);
        Assert.Equal(4096, config.Llm.Parameters.MaxTokens);
        Assert.Equal(3, config.Resilience.MaxRetries);
        Assert.True(config.Security.EnforceInternalServiceAuth);
        Assert.Equal("autonomous_tool_calling", config.Agent.Mode);
        Assert.True(config.Audio.Enabled);
        Assert.Equal("auto", config.Audio.Provider);
        Assert.Equal("config/vocabulary.yaml", config.Audio.VocabularyPath);
        Assert.Equal(1800, config.Audio.Engine.TimeoutSeconds);
        Assert.Equal("gemini-2.5-flash", config.Audio.Gemini.Model);
        Assert.Equal(100, config.Audio.MaxFileSizeMb);
        Assert.NotNull(config.McpUrls);
        Assert.Equal("http://localhost:8084", config.McpUrls.Calendar);
        Assert.NotNull(config.Calendar);
        Assert.True(config.Calendar.Enabled);
        Assert.Equal("http://localhost:8084", config.Calendar.ServiceUrl);
    }
}
