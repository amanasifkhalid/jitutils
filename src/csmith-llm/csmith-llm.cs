using Azure;
using Azure.AI.OpenAI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using OpenAI.Chat;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

internal sealed class Program
{
    static readonly string CORE_ROOT = Environment.GetEnvironmentVariable("CORE_ROOT") ?? throw new InvalidOperationException("Environment variable 'CORE_ROOT' is not set.");
    static readonly string CSMITH_PATH = Environment.GetEnvironmentVariable("CSMITH_PATH") ?? throw new InvalidOperationException("Environment variable 'CSMITH_PATH' is not set.");
    static readonly string OPENAI_ENDPOINT = Environment.GetEnvironmentVariable("OPENAI_ENDPOINT") ?? throw new InvalidOperationException("Environment variable 'OPENAI_ENDPOINT' is not set.");
    static readonly string OPENAI_API_KEY = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? throw new InvalidOperationException("Environment variable 'OPENAI_API_KEY' is not set.");
    static readonly string DEPLOYMENT_NAME = "gpt-4.1";
    static readonly string TEST_DLL = "test.dll";
    static readonly int MAX_PROGRAM_LENGTH = 20000;

    static readonly CSharpCompilationOptions ReleaseOptions =
        new CSharpCompilationOptions(OutputKind.ConsoleApplication, concurrentBuild: false, optimizationLevel: OptimizationLevel.Release).WithAllowUnsafe(true);

    static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(Path.Combine(CORE_ROOT, "System.Private.CoreLib.dll")),
        MetadataReference.CreateFromFile(Path.Combine(CORE_ROOT, "System.Runtime.dll")),
        MetadataReference.CreateFromFile(Path.Combine(CORE_ROOT, "System.Console.dll")),
        MetadataReference.CreateFromFile(Path.Combine(CORE_ROOT, "System.Linq.dll")),
        MetadataReference.CreateFromFile(Path.Combine(CORE_ROOT, "System.Collections.dll")),
        MetadataReference.CreateFromFile(Path.Combine(CORE_ROOT, "System.Collections.Immutable.dll")),
    };

    static readonly bool Verbose = true;

    AzureOpenAIClient azureClient;
    ChatClient chatClient;
    string csmithArgs;
    int numFailed;

    static void Log(string msg)
    {
        if (Verbose)
        {
            Console.WriteLine(msg);
        }
    }

    Program(string args)
    {
        csmithArgs = args;
        numFailed = 0;
        azureClient = new(
            new Uri(OPENAI_ENDPOINT),
            new AzureKeyCredential(OPENAI_API_KEY));
        chatClient = azureClient.GetChatClient(DEPLOYMENT_NAME);
        chatClient.CompleteChat([new SystemChatMessage("You are an expert C++ and C# programmer.")]);
    }

    string GenerateCppProgram(string csmithArgs)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = CSMITH_PATH,
                Arguments = csmithArgs,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        return output;
    }

    string ConvertCppProgramToCSharp(string cppProgram)
    {
        var response = chatClient.CompleteChat([
            new UserChatMessage("Transform this C++ code into equivalent C# code. Return only the C# program. Don't include any comments or notes."),
            new UserChatMessage(cppProgram)]);
        string csProgram = response.Value.Content[0].Text;
        csProgram = csProgram.Replace("```csharp", string.Empty).Replace("```", string.Empty);
        return csProgram;
    }

    string TryRepairingCSharpProgram(string originalProgram, string errorMsg)
    {
        var response = chatClient.CompleteChat([
            new UserChatMessage("The program produced the following compiler errors."),
            new UserChatMessage(errorMsg),
            new UserChatMessage("Fix these errors in the following program. Return only the corrected C# program. Don't include any comments or notes."),
            new UserChatMessage(originalProgram)]);
        string csProgram = response.Value.Content[0].Text;
        csProgram = csProgram.Replace("```csharp", string.Empty).Replace("```", string.Empty);
        return csProgram;
    }

    EmitResult CompileProgram(string csProgram)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(csProgram);
        SyntaxTree[] trees = { tree };
        CSharpCompilation compilation = CSharpCompilation.Create(null, new[] { tree }, References, ReleaseOptions);
        using var ms = new FileStream(TEST_DLL, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        return compilation.Emit(ms);
    }

    int RunProgram()
    {
        ProcessStartInfo psi = new ProcessStartInfo
        {
            FileName = Path.Combine(CORE_ROOT, "corerun"),
            Arguments = TEST_DLL,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.EnvironmentVariables["DOTNET_TieredCompilation"] = "0";
        Log("Running program");

        using (Process process = Process.Start(psi))
        {
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            int exitCode = process.ExitCode;

            if (exitCode != 0)
            {
                Log($"Program exited with code {exitCode}");
                Log($"Standard output: \n{stdout}\n");
                Log($"Standard error: \n{stderr}\n");
            }
            else
            {
                Log("Program ran successfully\n");
            }

            return exitCode;
        }
    }

    void Run()
    {
        while (true)
        {
            string cppProgram = GenerateCppProgram(csmithArgs);
            if (cppProgram.Length > MAX_PROGRAM_LENGTH)
            {
                continue;
            }

            Log($"C++ Program: \n{cppProgram}\n");

            string csProgram = ConvertCppProgramToCSharp(cppProgram);
            Log($"C# Program: \n{csProgram}\n");

            EmitResult result = CompileProgram(csProgram);
            if (!result.Success)
            {
                string errorMsg = string.Join('\n', result.Diagnostics.Select(error => error.ToString()));
                Log(errorMsg);

                csProgram = TryRepairingCSharpProgram(csProgram, errorMsg);
                Log($"Updated C# Program: \n{csProgram}\n");
                result = CompileProgram(csProgram);
            }

            if (result.Success)
            {
                int exitCode = RunProgram();
                if (exitCode != 0)
                {
                    File.WriteAllText($"fail{numFailed}.cs", csProgram);
                    numFailed++;
                }
            }
            else
            {
                Log(string.Join('\n', result.Diagnostics.Select(error => error.ToString())));
            }
        }
    }

    public static void Main(string[] args)
    {
        string csmithArgs = String.Join(' ', args);
        Program p = new(csmithArgs);
        p.Run();
    }
}
