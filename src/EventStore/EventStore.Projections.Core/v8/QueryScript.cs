// Copyright (c) 2012, Event Store LLP
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are
// met:
// 
// Redistributions of source code must retain the above copyright notice,
// this list of conditions and the following disclaimer.
// Redistributions in binary form must reproduce the above copyright
// notice, this list of conditions and the following disclaimer in the
// documentation and/or other materials provided with the distribution.
// Neither the name of the Event Store LLP nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
// HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
// 

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using EventStore.Core.Util;

namespace EventStore.Projections.Core.v8
{
    internal class QueryScript : IDisposable
    {
        private readonly PreludeScript _prelude;
        private readonly CompiledScript _script;
        private readonly Dictionary<string, IntPtr> _registeredHandlers = new Dictionary<string, IntPtr>();

        private Func<string, string[], string> _getStatePartition;
        private Func<string, string[], string> _processEvent;
        private Func<string> _transformStateToResult;
        private Action<string> _setState;
        private Action _initialize;
        private Func<string> _getSources;

        // the following two delegates must be kept alive while used by unmanaged code
        private readonly Js1.CommandHandlerRegisteredDelegate _commandHandlerRegisteredCallback; // do not inline
        private readonly Js1.ReverseCommandHandlerDelegate _reverseCommandHandlerDelegate; // do not inline
        private QuerySourcesDefinition _sources;
        private Exception _reverseCommandHandlerException;

        public event Action<string> Emit;

        public QueryScript(PreludeScript prelude, string script, string fileName)
        {
            _prelude = prelude;
            _commandHandlerRegisteredCallback = CommandHandlerRegisteredCallback;
            _reverseCommandHandlerDelegate = ReverseCommandHandler;

            _script = CompileScript(prelude, script, fileName);

            try
            {
                GetSources();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private CompiledScript CompileScript(PreludeScript prelude, string script, string fileName)
        {
            prelude.ScheduleTerminateExecution();
            IntPtr query = Js1.CompileQuery(
                prelude.GetHandle(), script, fileName, _commandHandlerRegisteredCallback, _reverseCommandHandlerDelegate);
            var terminated = prelude.CancelTerminateExecution();
            CompiledScript.CheckResult(query, terminated, disposeScriptOnException: true);
            return new CompiledScript(query, fileName);
        }

        private void ReverseCommandHandler(string commandName, string commandBody)
        {
            try
            {
                switch (commandName)
                {
                    case "emit":
                        DoEmit(commandBody);
                        break;
                    default:
                        Console.WriteLine("Ignoring unknown reverse command: '{0}'", commandName);
                        break;
                }
            }
            catch (Exception ex)
            {
                // report only the first exception occured in reverse command handler
                if (_reverseCommandHandlerException == null)
                    _reverseCommandHandlerException = ex;
            }
        }

        private void CommandHandlerRegisteredCallback(string commandName, IntPtr handlerHandle)
        {
            _registeredHandlers.Add(commandName, handlerHandle);
            //TODO: change to dictionary
            switch (commandName)
            {
                case "initialize":
                    _initialize = () => ExecuteHandler(handlerHandle, "");
                    break;
                case "get_state_partition":
                    _getStatePartition = (json, other) => ExecuteHandler(handlerHandle, json, other);
                    break;
                case "process_event":
                    _processEvent = (json, other) => ExecuteHandler(handlerHandle, json, other);
                    break;
                case "transform_state_to_result":
                    _transformStateToResult = () => ExecuteHandler(handlerHandle, "");
                    break;
                case "test_array":
                    break;
                case "set_state":
                    _setState = json => ExecuteHandler(handlerHandle, json);
                    break;
                case "get_sources":
                    _getSources = () => ExecuteHandler(handlerHandle, "");
                    break;
                case "set_debugging":
                    // ignore - browser based debugging only
                    break;
                default:
                    Console.WriteLine(
                        string.Format("Unknown command handler registered. Command name: {0}", commandName));
                    break;
            }
        }

        private void DoEmit(string commandBody)
        {
            OnEmit(commandBody);
        }

        private void GetSources()
        {
            if (_getSources == null)
                throw new InvalidOperationException("'get_sources' command handler has not been registered");
            var sourcesJson = _getSources();

            Console.WriteLine(sourcesJson);

            _sources = sourcesJson.ParseJson<QuerySourcesDefinition>();

            if (_sources.AllStreams)
                Console.WriteLine("All streams requested");
            else
            {
                foreach (var category in _sources.Categories)
                    Console.WriteLine("Category {0} requested", category);
                foreach (var stream in _sources.Streams)
                    Console.WriteLine("Stream {0} requested", stream);
            }
            if (_sources.AllEvents)
                Console.WriteLine("All events requested");
            else
            {
                foreach (var @event in _sources.Events)
                    Console.WriteLine("Event {0} requested", @event);
            }
        }

        private string ExecuteHandler(IntPtr commandHandlerHandle, string json, string[] other = null)
        {
            _reverseCommandHandlerException = null;

            _prelude.ScheduleTerminateExecution();

            IntPtr resultJsonPtr;
            IntPtr memoryHandle;
            bool success = Js1.ExecuteCommandHandler(
                _script.GetHandle(), commandHandlerHandle, json, other, other != null ? other.Length : 0,
                out resultJsonPtr, out memoryHandle);

            var terminated = _prelude.CancelTerminateExecution();
            if (!success)
                CompiledScript.CheckResult(_script.GetHandle(), terminated, disposeScriptOnException: false);
            string resultJson = Marshal.PtrToStringUni(resultJsonPtr);
            Js1.FreeResult(memoryHandle);
            if (_reverseCommandHandlerException != null)
            {
                throw new ApplicationException(
                    "An exception occurred while executing a reverse command handler. " + _reverseCommandHandlerException.Message,
                    _reverseCommandHandlerException);
            }
            return resultJson;
        }

        private void OnEmit(string obj)
        {
            Action<string> handler = Emit;
            if (handler != null) handler(obj);
        }

        public void Dispose()
        {
            _script.Dispose();
        }

        public void Initialize()
        {
            InitializeScript();
        }

        private void InitializeScript()
        {
            if (_initialize != null)
                _initialize();
        }

        public string GetPartition(string json, string[] other)
        {
            if (_getStatePartition == null)
                throw new InvalidOperationException("'get_state_partition' command handler has not been registered");

            return _getStatePartition(json, other);
        }

        public string Push(string json, string[] other)
        {
            if (_processEvent == null)
                throw new InvalidOperationException("'process_event' command handler has not been registered");

            return _processEvent(json, other);
        }

        public string TransformStateToResult()
        {
            if (_transformStateToResult == null)
                throw new InvalidOperationException("'transform_state_to_result' command handler has not been registered");

            return _transformStateToResult();
        }

        public void SetState(string state)
        {
            if (_setState == null)
                throw new InvalidOperationException("'set_state' command handler has not been registered");
            _setState(state);
        }

        public QuerySourcesDefinition GetSourcesDefintion()
        {
            return _sources;
        }

        [DataContract]
        internal class QuerySourcesDefinition
        {
            [DataMember(Name = "all_streams")]
            public bool AllStreams { get; set; }

            [DataMember(Name = "categories")]
            public string[] Categories { get; set; }

            [DataMember(Name = "streams")]
            public string[] Streams { get; set; }

            [DataMember(Name = "all_events")]
            public bool AllEvents { get; set; }

            [DataMember(Name = "events")]
            public string[] Events { get; set; }

            [DataMember(Name = "by_streams")]
            public bool ByStreams { get; set; }

            [DataMember(Name = "by_custom_partitions")]
            public bool ByCustomPartitions { get; set; }

            [DataMember(Name = "defines_state_transform")]
            public bool DefinesStateTransform { get; set; }

            [DataMember(Name = "options")]
            public QuerySourcesDefinitionOptions Options { get; set;}
        }

        [DataContract]
        internal class QuerySourcesDefinitionOptions
        {
            [DataMember(Name = "resultStreamName")]
            public string ResultStreamName { get; set; }

            [DataMember(Name = "partitionResultStreamNamePattern")]
            public string PartitionResultStreamNamePattern { get; set; }

            [DataMember(Name = "useEventIndexes")]
            public bool UseEventIndexes { get; set; }

            [DataMember(Name = "$forceProjectionName")]
            public string ForceProjectionName { get; set; }

            [DataMember(Name = "reorderEvents")]
            public bool ReorderEvents { get; set; }

            [DataMember(Name = "processingLag")]
            public int? ProcessingLag { get; set; }

        }
    }
}
