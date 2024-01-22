using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.PowerShell.Commands;

namespace RemoteForge.Commands;

// internal class InvokeCommandJob : IDisposable
// {
//     public readonly Runspace _runspace;
//     public readonly string _command;

//     public InvokeCommandJob(
//         StringForgeConnectionInfoPSSession connectionInfo,
//         string command,
//         PSObject?[]? argumentList = null,
//         IDictionary? parameters = null)
//     {
//         _command = command;
//         _runspace = RunspaceFactory.CreateRunspace();
//     }

//     public void Start()
//     {

//     }

//     public void WriteInput(PSObject? inputObj)
//     {

//     }

//     public void CompleteInput()
//     {

//     }

//     public void Dispose()
//     {
//         _runspace?.Dispose();
//         GC.SuppressFinalize(this);
//     }
// }

[Cmdlet(
    VerbsLifecycle.Invoke,
    "Remote",
    DefaultParameterSetName = "ScriptBlockArgList"
)]
[OutputType(typeof(object))]
public sealed class InvokeRemoteCommand : PSCmdlet, IDisposable
{
    private CancellationTokenSource _cancelToken = new();
    private Task? _worker;
    private PSDataCollection<PSObject?> _inputPipe = new();
    private BlockingCollection<PSObject?> _outputPipe = new();

    [Parameter(
        Mandatory = true,
        Position = 0
    )]
    [Alias("ComputerName", "Cn")]
    public StringForgeConnectionInfoPSSession[] ConnectionInfo { get; set; } = Array.Empty<StringForgeConnectionInfoPSSession>();

    [Parameter(
        Mandatory = true,
        Position = 1,
        ParameterSetName = "ScriptBlockArgList"
    )]
    [Parameter(
        Mandatory = true,
        Position = 1,
        ParameterSetName = "ScriptBlockParam"
    )]
    public ScriptBlock? ScriptBlock { get; set; }

    [Parameter(
        Mandatory = true,
        Position = 1,
        ParameterSetName = "FilePathArgList"
    )]
    [Parameter(
        Mandatory = true,
        Position = 1,
        ParameterSetName = "FilePathParam"
    )]
    [Alias("PSPath")]
    public string FilePath { get; set; } = string.Empty;

    [Parameter(
        Position = 2,
        ParameterSetName = "ScriptBlockArgList"
    )]
    [Parameter(
        Position = 2,
        ParameterSetName = "FilePathArgList"
    )]
    [Alias("Args")]
    public PSObject?[] ArgumentList { get; set; } = Array.Empty<PSObject>();

    [Parameter(
        Position = 2,
        ParameterSetName = "ScriptBlockParam"
    )]
    [Parameter(
        Position = 2,
        ParameterSetName = "FilePathParam"
    )]
    [Alias("Params")]
    public IDictionary? ParamSplat { get; set; }

    [Parameter(
        ValueFromPipeline = true,
        ValueFromPipelineByPropertyName = true
    )]
    public PSObject?[] InputObject { get; set; } = Array.Empty<PSObject>();

    [Parameter]
    public int ThrottleLimit { get; set; } = 32;

    // [Parameter(ValueFromRemainingArguments = true)]
    // public PSObject?[] UnboundArguments { get; set; } = Array.Empty<PSObject?>();

    protected override void BeginProcessing()
    {
        string commandToRun;
        if (ScriptBlock != null)
        {
            commandToRun = ScriptBlock.ToString();
        }
        else
        {
            string resolvedPath = SessionState.Path.GetUnresolvedProviderPathFromPSPath(
                FilePath,
                out ProviderInfo provider,
                out PSDriveInfo _);

            if (provider.ImplementingType != typeof(FileSystemProvider))
            {
                ErrorRecord err = new(
                    new ArgumentException($"The resolved path '{resolvedPath}' is not a FileSystem path but {provider.Name}"),
                    "FilePathNotFileSystem",
                    ErrorCategory.InvalidArgument,
                    FilePath);
                ThrowTerminatingError(err);
            }
            else if (!File.Exists(resolvedPath))
            {
                ErrorRecord err = new(
                    new FileNotFoundException($"Cannot find path '{resolvedPath}' because it does not exist", resolvedPath),
                    "FilePathNotFound",
                    ErrorCategory.ObjectNotFound,
                    FilePath);
                ThrowTerminatingError(err);
            }

            commandToRun = File.ReadAllText(resolvedPath);
        }

        // TODO: Check if `$using:...` is used in commandToRun
        Dictionary<string, PSObject?> parameters = new();
        if (ParamSplat?.Count > 0)
        {
            foreach (DictionaryEntry kvp in ParamSplat)
            {
                PSObject? value = null;
                if (kvp.Value is PSObject psObject)
                {
                    value = psObject;
                }
                else if (kvp.Value != null)
                {
                    value = PSObject.AsPSObject(kvp.Value);
                }

                parameters.Add(kvp.Key?.ToString() ?? "", value);
            }
        }
        _worker = Task.Run(async () => await RunWorker(
            commandToRun,
            arguments: ArgumentList,
            parameters: parameters));
    }

    protected override void ProcessRecord()
    {
        foreach (PSObject? input in InputObject)
        {
            _inputPipe.Add(input);
        }

        // See if there is any output already ready to write.
        PSObject? currentOutput = null;
        while (_outputPipe.TryTake(out currentOutput, 0, _cancelToken.Token)) {
            WriteObject(currentOutput);
        }
    }

    protected override void EndProcessing()
    {
        _inputPipe.Complete();

        foreach (PSObject? output in _outputPipe.GetConsumingEnumerable(_cancelToken.Token))
        {
            WriteObject(output);
        }
        _worker?.Wait(-1, _cancelToken.Token);
    }

    protected override void StopProcessing()
    {
        _cancelToken.Cancel();
    }

    private async Task RunWorker(
        string script,
        PSObject?[] arguments,
        IDictionary? parameters)
    {
        try
        {
            Queue<StringForgeConnectionInfoPSSession> incomingConnections = new(ConnectionInfo);

            foreach (StringForgeConnectionInfoPSSession connInfo in incomingConnections)
            {
                Runspace rs = RunspaceFactory.CreateRunspace();
                rs.OpenAsync();
                // wait for open

                PowerShell ps = PowerShell.Create(rs);
                ps.AddScript(script);
                if (arguments != null)
                {
                    foreach (PSObject? obj in arguments)
                    {
                        ps.AddArgument(obj);
                    }
                }
                if (parameters != null)
                {
                    ps.AddParameters(parameters);
                }
                await ps.InvokeAsync(_inputPipe);
            }
        }
        finally
        {
            _outputPipe.CompleteAdding();
        }
    }

    public void Dispose()
    {
        _cancelToken?.Dispose();
        _inputPipe?.Dispose();
        _outputPipe?.Dispose();
        GC.SuppressFinalize(this);
    }
}

internal class ConnectionWorker
{
    private readonly StringForgeConnectionInfoPSSession _info;
    private readonly PSDataCollection<PSObject?> _inputPipe;

    public ConnectionWorker(
        StringForgeConnectionInfoPSSession info,
        PSDataCollection<PSObject?> inputPipe,
        BlockingCollection<PSObject?> outputPipe)
    {
        _info = info;
        _inputPipe = inputPipe;
    }

    public async Task Start(
        string script,
        PSObject?[] arguments,
        IDictionary? parameters)
    {
        bool disposeRunspace = false;
        Runspace? runspace = _info.PSSession?.Runspace;
        if (runspace == null)
        {
            disposeRunspace = true;
            runspace = await RunspaceHelper.CreateRunspaceAsync(
                _info.ConnectionInfo,
                default,
                host: null,
                typeTable: null,
                applicationArguments: null);
        }

        try
        {
            using PowerShell ps = PowerShell.Create(runspace);
            using PSDataCollection<PSObject?> outputPipe = new();
            ps.AddScript(script);
            if (arguments != null)
            {
                foreach (PSObject? obj in arguments)
                {
                    ps.AddArgument(obj);
                }
            }
            if (parameters != null)
            {
                ps.AddParameters(parameters);
            }
            await ps.InvokeAsync(_inputPipe, outputPipe);
        }
        finally
        {
            if (disposeRunspace)
            {
                runspace.Dispose();
            }
        }
    }
}
