using JRETS.Go.Core.Configuration;

namespace JRETS.Go.Core.Services;

public interface IScoringConfigurationLoader
{
    ScoringConfiguration LoadFromFile(string filePath);
}
