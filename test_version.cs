using System;
using Azure.AI.OpenAI;

class TestVersion {
    static void Main() {
        var versions = Enum.GetNames(typeof(AzureOpenAIClientOptions.ServiceVersion));
        foreach (var v in versions) {
            Console.WriteLine(v);
        }
    }
}
