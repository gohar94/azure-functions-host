﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Azure.WebJobs.Script.Binding;
using Microsoft.Azure.WebJobs.Script.Workers;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Microsoft.Azure.WebJobs.Script.Description
{
    internal class WorkerFunctionInvoker : FunctionInvokerBase
    {
        private readonly Collection<FunctionBinding> _inputBindings;
        private readonly Collection<FunctionBinding> _outputBindings;
        private readonly BindingMetadata _bindingMetadata;
        private readonly ILogger _logger;
        private readonly Action<ScriptInvocationResult> _handleScriptReturnValue;
        private readonly IFunctionInvocationDispatcher _functionDispatcher;
        private readonly IApplicationLifetime _applicationLifetime;

        internal WorkerFunctionInvoker(ScriptHost host, BindingMetadata bindingMetadata, FunctionMetadata functionMetadata, ILoggerFactory loggerFactory,
            Collection<FunctionBinding> inputBindings, Collection<FunctionBinding> outputBindings, IFunctionInvocationDispatcher functionDispatcher, IApplicationLifetime applicationLifetime)
            : base(host, functionMetadata, loggerFactory)
        {
            _bindingMetadata = bindingMetadata;
            _inputBindings = inputBindings;
            _outputBindings = outputBindings;
            _functionDispatcher = functionDispatcher;
            _logger = loggerFactory.CreateLogger<WorkerFunctionInvoker>();
            _applicationLifetime = applicationLifetime;

            InitializeFileWatcherIfEnabled();

            if (_outputBindings.Any(p => p.Metadata.IsReturn))
            {
                _handleScriptReturnValue = HandleReturnParameter;
            }
            else
            {
                _handleScriptReturnValue = HandleOutputDictionary;
            }
        }

        protected override async Task<object> InvokeCore(object[] parameters, FunctionInvocationContext context)
        {
            // Need to wait for at least one language worker process to be initialized before accepting invocations
            if (!IsDispatcherReady())
            {
                await DelayUntilFunctionDispatcherInitializedOrShutdown();
            }

            var bindingData = context.Binder.BindingData;
            object triggerValue = TransformInput(parameters[0], bindingData);
            var triggerInput = (_bindingMetadata.Name, _bindingMetadata.DataType ?? DataType.String, triggerValue);
            IEnumerable<(string, DataType, object)> inputs = new[] { triggerInput };
            if (_inputBindings.Count > 1)
            {
                var nonTriggerInputs = await BindInputsAsync(context.Binder);
                inputs = inputs.Concat(nonTriggerInputs);
            }

            var invocationContext = new ScriptInvocationContext
            {
                FunctionMetadata = Metadata,
                BindingData = bindingData,
                ExecutionContext = context.ExecutionContext,
                Inputs = inputs,
                ResultSource = new TaskCompletionSource<ScriptInvocationResult>(),
                AsyncExecutionContext = System.Threading.ExecutionContext.Capture(),
                Traceparent = Activity.Current?.Id,
                Tracestate = Activity.Current?.TraceStateString,
                Attributes = Activity.Current?.Tags,

                // TODO: link up cancellation token to parameter descriptors
                CancellationToken = CancellationToken.None,
                Logger = context.Logger
            };

            string invocationId = context.ExecutionContext.InvocationId.ToString();
            _logger.LogTrace($"Sending invocation id:{invocationId}");
            await _functionDispatcher.InvokeAsync(invocationContext);
            var result = await invocationContext.ResultSource.Task;

            await BindOutputsAsync(triggerValue, context.Binder, result);
            return result.Return;
        }

        private bool IsDispatcherReady()
        {
            return _functionDispatcher.State == FunctionInvocationDispatcherState.Initialized || _functionDispatcher.State == FunctionInvocationDispatcherState.Default;
        }

        private async Task DelayUntilFunctionDispatcherInitializedOrShutdown()
        {
            // Don't delay if functionDispatcher is already initialized OR is skipping initialization for one of
            // these reasons: started in placeholder, has no functions, functions do not match set language.

            if (!IsDispatcherReady())
            {
                _logger.LogTrace($"FunctionDispatcher state: {_functionDispatcher.State}");
                bool result = await Utility.DelayAsync((_functionDispatcher.ErrorEventsThreshold + 1) * WorkerConstants.ProcessStartTimeoutSeconds, WorkerConstants.WorkerReadyCheckPollingIntervalMilliseconds, () =>
                {
                    return _functionDispatcher.State != FunctionInvocationDispatcherState.Initialized;
                });

                if (result)
                {
                    _logger.LogError($"Final functionDispatcher state: {_functionDispatcher.State}. Initialization timed out and host is shutting down");
                    _applicationLifetime.StopApplication();
                }
            }
        }

        private void Log(string tag, DateTime start, DateTime end)
        {
            string startLog = start.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            string endLog = end.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            double elapsedMs = (end - start).TotalMilliseconds;
            Console.WriteLine($"{tag},{startLog},{endLog},{elapsedMs}");
        }

        private async Task<(string name, DataType type, object value)[]> BindInputsAsync(Binder binder)
        {
            DateTime globalStart = DateTime.UtcNow;

            var bindingTasks = _inputBindings
                .AsParallel()
                .Where(binding => !binding.Metadata.IsTrigger)
                .Select(async (binding) =>
                {
                    DateTime taskStart = DateTime.UtcNow;

                    BindingContext bindingContext = new BindingContext
                    {
                        Binder = binder,
                        BindingData = binder.BindingData,
                        DataType = binding.Metadata.DataType ?? DataType.String,
                        Cardinality = binding.Metadata.Cardinality ?? Cardinality.One
                    };

                    await binding.BindAsync(bindingContext).ConfigureAwait(false);

                    DateTime taskEnd = DateTime.UtcNow;
                    Log(binding.Metadata.Name, taskStart, taskEnd);

                    return (binding.Metadata.Name, bindingContext.DataType, bindingContext.Value);
                });

            var ret = await Task.WhenAll(bindingTasks);

            DateTime globalEnd = DateTime.UtcNow;
            Log("AllInputs", globalStart, globalEnd);

            return ret;
        }

        private async Task BindOutputsAsync(object input, Binder binder, ScriptInvocationResult result)
        {
            if (_outputBindings == null)
            {
                return;
            }

            _handleScriptReturnValue(result);

            DateTime globalStart = DateTime.UtcNow;

            var outputBindingTasks = _outputBindings.AsParallel().Select(async binding =>
            {
                DateTime taskStart = DateTime.UtcNow;

                // apply the value to the binding
                if (result.Outputs.TryGetValue(binding.Metadata.Name, out object value) && value != null)
                {
                    BindingContext bindingContext = new BindingContext
                    {
                        TriggerValue = input,
                        Binder = binder,
                        BindingData = binder.BindingData,
                        Value = value
                    };
                    await binding.BindAsync(bindingContext).ConfigureAwait(false);

                    DateTime taskEnd = DateTime.UtcNow;
                    Log(binding.Metadata.Name, taskStart, taskEnd);
                }
            });

            await Task.WhenAll(outputBindingTasks);

            DateTime globalEnd = DateTime.UtcNow;
            Log("AllOutputs", globalStart, globalEnd);
        }

        private object TransformInput(object input, Dictionary<string, object> bindingData)
        {
            if (input is Stream)
            {
                var dataType = _bindingMetadata.DataType ?? DataType.String;
                FunctionBinding.ConvertStreamToValue((Stream)input, dataType, ref input);
            }

            // TODO: investigate moving POCO style binding addition to sdk
            Utility.ApplyBindingData(input, bindingData);
            return input;
        }

        private void HandleReturnParameter(ScriptInvocationResult result)
        {
            result.Outputs[ScriptConstants.SystemReturnParameterBindingName] = result.Return;
        }

        private void HandleOutputDictionary(ScriptInvocationResult result)
        {
            if (result.Return is JObject returnJson)
            {
                foreach (var pair in returnJson)
                {
                    result.Outputs[pair.Key] = pair.Value.ToObject<object>();
                }
            }
        }
    }
}