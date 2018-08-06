﻿#if PLAT_SPANS
using ProtoBuf.Meta;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace ProtoBuf
{
    public partial class ProtoReader
    {
        /// <summary>
        /// Creates a new reader against a multi-segment buffer
        /// </summary>
        /// <param name="source">The source buffer</param>
        /// <param name="state">Reader state</param>
        /// <param name="model">The model to use for serialization; this can be null, but this will impair the ability to deserialize sub-objects</param>
        /// <param name="context">Additional context about this serialization operation</param>
        public static ProtoReader Create(out State state, ReadOnlySequence<byte> source, TypeModel model, SerializationContext context = null)
        {
            // disabled for performance tuning
            //if(source.IsSingleSegment)
            //{
            //    return Create(out state, source.First, model, context);
            //}

            var reader = ReadOnlySequenceProtoReader.GetRecycled()
                ?? new ReadOnlySequenceProtoReader();
            reader.Init(out state, source, model, context);
            return reader;
        }

        private sealed class ReadOnlySequenceProtoReader : ProtoReader
        {
            [ThreadStatic]
            private static ReadOnlySequenceProtoReader s_lastReader;
            private ReadOnlySequence<byte>.Enumerator _source;

            internal static ReadOnlySequenceProtoReader GetRecycled()
            {
                var tmp = s_lastReader;
                s_lastReader = null;
                return tmp;
            }

            internal override void Recycle()
            {
                Dispose();
                s_lastReader = this;
            }

            public override void Dispose()
            {
                base.Dispose();
                _source = default;
            }

            internal void Init(out State state, ReadOnlySequence<byte> source, TypeModel model, SerializationContext context)
            {
                base.Init(model, context);
                _source = source.GetEnumerator();
                state = default;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private int GetSomeData(ref State state, bool throwIfEOF = true)
            {
                var data = state.RemainingInCurrent;
                return data == 0 ? ReadNextBuffer(ref state, throwIfEOF) : data;
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            private int ReadNextBuffer(ref State state, bool throwIfEOF)
            {
                do
                {
                    if (!_source.MoveNext())
                    {
                        if (throwIfEOF) ThrowEoF(this);
                        return 0;
                    }
                    state.Init(_source.Current);
                } while (state.Span.IsEmpty);
                return state.Span.Length;
            }

            private protected override int ImplTryReadUInt64VarintWithoutMoving(ref State state, out ulong value)
            {
                return state.RemainingInCurrent >= 10
                    ? ReadOnlyMemoryProtoReader.TryParseUInt64Varint(this, state.OffsetInCurrent, out value, state.Span)
                    : ViaStackAlloc(ref state, out value);

                int ViaStackAlloc(ref State s, out ulong val)
                {
                    Span<byte> span = stackalloc byte[10];
                    Span<byte> target = span;

                    int available = 0;
                    if (s.RemainingInCurrent != 0)
                    {
                        int take = Math.Min(s.RemainingInCurrent, target.Length);
                        Peek(ref s, take).CopyTo(target);
                        target = target.Slice(available);
                        available += take;
                    }

                    var iterCopy = _source;
                    while (!target.IsEmpty && iterCopy.MoveNext())
                    {
                        var nextBuffer = iterCopy.Current.Span;
                        var take = Math.Min(nextBuffer.Length, target.Length);

                        nextBuffer.Slice(0, take).CopyTo(target);
                        target = target.Slice(take);
                        available += take;
                    }

                    if (available != 10) span = span.Slice(0, available);
                    return ReadOnlyMemoryProtoReader.TryParseUInt64Varint(this, 0, out val, span);
                }
            }

            private protected override uint ImplReadUInt32Fixed(ref State state)
            {
                return state.RemainingInCurrent >= 4
                    ? BinaryPrimitives.ReadUInt32LittleEndian(Consume(ref state, 4))
                    : ViaStackAlloc(ref state);

                uint ViaStackAlloc(ref State st)
                {
                    Span<byte> span = stackalloc byte[4];
                    // manually inline ImplReadBytes because of compiler restriction
                    var target = span;
                    while (!target.IsEmpty)
                    {
                        var take = Math.Min(GetSomeData(ref st), target.Length);
                        Consume(ref st, take).CopyTo(target);
                        target = target.Slice(take);
                    }
                    return BinaryPrimitives.ReadUInt32LittleEndian(span);
                }
            }

            private protected override ulong ImplReadUInt64Fixed(ref State state)
            {
                return state.RemainingInCurrent >= 8
                    ? BinaryPrimitives.ReadUInt64LittleEndian(Consume(ref state, 8))
                    : ViaStackAlloc(ref state);

                ulong ViaStackAlloc(ref State st)
                {
                    Span<byte> span = stackalloc byte[8];
                    // manually inline ImplReadBytes because of compiler restriction
                    var target = span;
                    while (!target.IsEmpty)
                    {
                        var take = Math.Min(GetSomeData(ref st), target.Length);
                        Consume(ref st, take).CopyTo(target);
                        target = target.Slice(take);
                    }
                    return BinaryPrimitives.ReadUInt64LittleEndian(span);
                }
            }

            private protected override string ImplReadString(ref State state, int bytes)
            {
                return state.RemainingInCurrent >= bytes
                    ? ReadOnlyMemoryProtoReader.ToString(Consume(
                        ref state, bytes, out var offset), offset, bytes)
                    : ImplReadStringMultiSegment(ref state, bytes);
            }

            private string ImplReadStringMultiSegment(ref State state, int bytes)
            {
                // we should probably do the work with a Decoder,
                // but this works for today
                using (var mem = MemoryPool<byte>.Shared.Rent(bytes))
                {
                    var span = mem.Memory.Span;
                    ImplReadBytes(ref state, span);
                    return ReadOnlyMemoryProtoReader.ToString(span, 0, bytes);
                }
            }

            private void ImplReadBytes(ref State state, Span<byte> target)
            {
                if (state.RemainingInCurrent >= target.Length) Consume(ref state, target.Length).CopyTo(target);
                else Looped(ref state, target);

                void Looped(ref State st, Span<byte> ttarget)
                {
                    while (!ttarget.IsEmpty)
                    {
                        var take = Math.Min(GetSomeData(ref st), ttarget.Length);
                        Consume(ref st, take).CopyTo(ttarget);
                        ttarget = ttarget.Slice(take);
                    }
                }
            }

            private protected override void ImplReadBytes(ref State state, ArraySegment<byte> target)
                => ImplReadBytes(ref state, new Span<byte>(target.Array, target.Offset, target.Count));

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private ReadOnlySpan<byte> Consume(ref State state, int bytes)
            {
                var span = state.Span.Slice(state.OffsetInCurrent, bytes);
                state.Consume(bytes);
                Advance(bytes);
                return span;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private ReadOnlySpan<byte> Consume(ref State state, int bytes, out int offset)
            {
                offset = state.OffsetInCurrent;
                state.Consume(bytes);
                Advance(bytes);
                return state.Span;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private ReadOnlySpan<byte> Peek(ref State state, int bytes)
                => state.Span.Slice(state.OffsetInCurrent, bytes);

            private protected override int ImplTryReadUInt32VarintWithoutMoving(ref State state, Read32VarintMode mode, out uint value)
            {
                return state.RemainingInCurrent >= 10
                    ? ReadOnlyMemoryProtoReader.TryParseUInt32Varint(this, state.OffsetInCurrent,
                        mode == Read32VarintMode.Signed, out value, state.Span)
                    : ViaStackAlloc(ref state, mode, out value);

                int ViaStackAlloc(ref State s, Read32VarintMode m, out uint val)
                {
                    Span<byte> span = stackalloc byte[10];
                    Span<byte> target = span;
                    var currentBuffer = Peek(ref s, Math.Min(target.Length, s.RemainingInCurrent));
                    currentBuffer.CopyTo(target);
                    int available = currentBuffer.Length;
                    target = target.Slice(available);

                    var iterCopy = _source;
                    while (!target.IsEmpty && iterCopy.MoveNext())
                    {
                        var nextBuffer = iterCopy.Current.Span;
                        var take = Math.Min(nextBuffer.Length, target.Length);

                        nextBuffer.Slice(0, take).CopyTo(target);
                        target = target.Slice(take);
                        available += take;
                    }
                    if (available != 10) span = span.Slice(0, available);
                    return ReadOnlyMemoryProtoReader.TryParseUInt32Varint(this, 0, m == Read32VarintMode.Signed, out val, span);
                }
            }

            private protected override void ImplSkipBytes(ref State state, long count)
            {
                if (state.RemainingInCurrent >= count) Skip(ref state, (int)count);
                else Looped(ref state, count);

                void Looped(ref State st, long ccount)
                {
                    while (ccount != 0)
                    {
                        var take = (int)Math.Min(GetSomeData(ref st), ccount);
                        Skip(ref st, take);
                        ccount -= take;
                    }
                }
            }
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void Skip(ref State state, int bytes)
            {
                state.Consume(bytes);
                Advance(bytes);
            }

            private protected override bool IsFullyConsumed(ref State state)
                => GetSomeData(ref state, false) == 0;
        }
    }
}
#endif