﻿using ProtoBuf.Meta;
using System;
using System.Buffers;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace ProtoBuf.Grpc.Configuration
{
    /// <summary>
    /// Provides protobuf-net implementation of a per-type marshaller
    /// </summary>
    public partial class ProtoBufMarshallerFactory : MarshallerFactory
    {
        /// <summary>
        /// Options that control protobuf-net marshalling
        /// </summary>
        [Flags]
        public enum Options
        {
            /// <summary>
            /// No options
            /// </summary>
            None = 0,
            /// <summary>
            /// Enforce that only contract-types should be allowed
            /// </summary>
            ContractTypesOnly = 1 << 0,
            /// <summary>
            /// Disable 'contextual' serializer usage (this means serializers that use <see cref="ReadOnlySequence{byte}"/> or <see cref="IBufferWriter{byte}"/>),
            /// using the legacy <see cref="byte[]"/> APIs.
            /// </summary>
            DisableContextualSerializer = 1 << 1,
        }

        /// <summary>
        /// Uses the default protobuf-net serializer
        /// </summary>
        public static MarshallerFactory Default { get; } = new ProtoBufMarshallerFactory(RuntimeTypeModel.Default, Options.None);

        private readonly TypeModel _model;
        private readonly SerializationContext? _userState;
        private readonly Options _options;
        // note: these are the same *object*, but pre-checked for optional API support, for efficiency
        // (the minimum .NET object size means that the extra fields don't cost anything)
        private readonly IMeasuredProtoOutput<IBufferWriter<byte>>? _measuredWriterModel;
        private readonly IProtoInput<ReadOnlySequence<byte>>? _squenceReaderModel;

#if ARRAY_BUFFER_WRITER
        private readonly IProtoOutput<IBufferWriter<byte>>? _bufferWriterModel;
#endif

        /// <summary>
        /// Create a new factory using a specific protobuf-net model
        /// </summary>
        public static MarshallerFactory Create(TypeModel? model = null, Options options = Options.None, object? userState = null)
        {
            model ??= RuntimeTypeModel.Default;
            if (options == Options.None && model == RuntimeTypeModel.Default) return Default;
            return new ProtoBufMarshallerFactory(model, options, userState);
        }

        /// <summary>
        /// Create a new factory using a specific protobuf-net model
        /// </summary>
        public static MarshallerFactory Create(RuntimeTypeModel? model, Options options)
            => Create((TypeModel?)model, options, null);

        internal ProtoBufMarshallerFactory(TypeModel model, Options options, object? userState = null)
        {
            _model = model;
            _options = options;
            _userState = userState switch
            {
                null => null,
                SerializationContext ctx => ctx,
                _ => new SerializationContext { Context = userState }
            };

            if (UseContextualSerializer)
            {
                // test these once rather than every time
                _measuredWriterModel = model as IMeasuredProtoOutput<IBufferWriter<byte>>;
                _squenceReaderModel = model as IProtoInput<ReadOnlySequence<byte>>;
            }

#if ARRAY_BUFFER_WRITER
            _bufferWriterModel = model as IProtoOutput<IBufferWriter<byte>>;
#endif
        }

        private bool UseContextualSerializer => (_options & Options.DisableContextualSerializer) == 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool Has(Options option) => (_options & option) == option;

        /// <summary>
        /// Deserializes an object from a payload
        /// </summary>
        protected internal override global::Grpc.Core.Marshaller<T> CreateMarshaller<T>()
            => UseContextualSerializer
            ? new global::Grpc.Core.Marshaller<T>(ContextualSerialize<T>, ContextualDeserialize<T>)
            : base.CreateMarshaller<T>();

#if DEBUG
        private static int _uplevelBufferReadCount, _uplevelBufferWriteCount;
        public static int UplevelBufferReadCount => Volatile.Read(ref _uplevelBufferReadCount);
        public static int UplevelBufferWriteCount => Volatile.Read(ref _uplevelBufferWriteCount);

        static partial void RecordUplevelBufferRead() => Interlocked.Increment(ref _uplevelBufferReadCount);
        static partial void RecordUplevelBufferWrite() => Interlocked.Increment(ref _uplevelBufferWriteCount);
#endif
        static partial void RecordUplevelBufferRead();
        static partial void RecordUplevelBufferWrite();

        private bool TryGetBufferWriter(global::Grpc.Core.SerializationContext context, out IBufferWriter<byte>? writer)
        {
            // the managed implementation does not yet implement this API
            try { writer = context.GetBufferWriter(); }
            catch (NotSupportedException) { writer = default; }
            catch (NotImplementedException) { writer = default; }
            return writer is object;
        }

        private void ContextualSerialize<T>(T value, global::Grpc.Core.SerializationContext context)
        {
            if (_measuredWriterModel is object)
            {   // forget what we think we know about TypeModel; if we have protobuf-net 3.*, we can do this

                RecordUplevelBufferWrite();

                using var measured = _measuredWriterModel.Measure(value, userState: _userState);
                int len = checked((int)measured.Length);
                context.SetPayloadLength(len);

                if (TryGetBufferWriter(context, out var writer))
                {   // write to the buffer-writer API
                    _measuredWriterModel.Serialize(measured, writer!);
                    context.Complete();
                }
                else
                {
                    // the buffer-writer API wasn't supported, but we can still optimize by right-sizing
                    // a MemoryStream to write to, to avoid a resize etc
                    context.Complete(Serialize<T>(value, len));
                }
            }
            else
            {
                context.Complete(Serialize<T>(value));
            }
        }

        private T ContextualDeserialize<T>(global::Grpc.Core.DeserializationContext context)
        {
            var ros = context.PayloadAsReadOnlySequence();
            if (_squenceReaderModel is object)
            {   // forget what we think we know about TypeModel; if we have protobuf-net 3.*, we can do this
                RecordUplevelBufferRead();
                return _squenceReaderModel.Deserialize<T>(ros, userState: _userState);
            }

            // 2.4.2+ can use array-segments
            IProtoInput<ArraySegment<byte>> segmentReader = _model;

            // can we go direct to a single segment?
            if (ros.IsSingleSegment && MemoryMarshal.TryGetArray(ros.First, out var segment))
            {
                return segmentReader.Deserialize<T>(segment, userState: _userState);
            }

            // otherwise; linearize the data
            var oversized = ArrayPool<byte>.Shared.Rent(context.PayloadLength);
            try
            {
                ros.CopyTo(oversized);
                return segmentReader.Deserialize<T>(new ArraySegment<byte>(oversized, 0, context.PayloadLength), userState: _userState);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(oversized);
            }
        }

        /// <summary>
        /// Indicates whether a type should be considered as a serializable data type
        /// </summary>
        protected internal override bool CanSerialize(Type type)
            => Has(Options.ContractTypesOnly)
                ? _model.CanSerializeContractType(type)
                : _model.CanSerialize(type);

        /// <summary>
        /// Deserializes an object from a payload
        /// </summary>
        protected override T Deserialize<T>(byte[] payload)
        {
            IProtoInput<byte[]> segmentReader = _model;
            return segmentReader.Deserialize<T>(payload, userState: _userState);
        }

        /// <summary>
        /// Serializes an object to a payload
        /// </summary>
        protected override byte[] Serialize<T>(T value)
        {
            using var ms = new MemoryStream();
            _model.Serialize(ms, value, context: null);
            return ms.ToArray();
        }

        private byte[] Serialize<T>(T value, int length)
        {
            if (length == 0) return Array.Empty<byte>();

#if ARRAY_BUFFER_WRITER
            if (_bufferWriterModel is object)
            {
                var abw = new ArrayBufferWriter<byte>(length);
                _bufferWriterModel.Serialize<T>(abw, value, userState: _userState);
                AssertLength(abw.WrittenCount);
                if (!(MemoryMarshal.TryGetArray(abw.WrittenMemory, out var segment)
                    && segment.Offset == 0 && segment.Count == segment.Array.Length))
                {
                    InvalidBufferWriter();
                }
                return segment.Array;

                static void InvalidBufferWriter()
                    => throw new InvalidOperationException($"After writing to {nameof(ArrayBufferWriter<byte>)}, the array was unavailable or invalid");
            }
#endif
            var arr = new byte[length];
            using var ms = new MemoryStream(arr);
            _model.Serialize(ms, value, context: null);
            AssertLength(ms.Length);
            return arr;

            void AssertLength(long actual)
            {
                if (length != actual) throw new InvalidOperationException(
                    $"Length miscalculated; expected {length}, got {actual}");
            }
        }
    }
}
