using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using EventStore.Client.Streams;
using Grpc.Core;

#nullable enable
namespace EventStore.Client {
	public partial class EventStoreClient {
		private async IAsyncEnumerable<ResolvedEvent> ReadAllAsync(
			Direction direction,
			Position position,
			long maxCount,
			EventStoreClientOperationOptions operationOptions,
			bool resolveLinkTos = false,
			UserCredentials? userCredentials = null,
			[EnumeratorCancellation] CancellationToken cancellationToken = default) {
			await foreach (var (confirmation, _, resolvedEvent) in ReadInternal(new ReadReq {
					Options = new ReadReq.Types.Options {
						ReadDirection = direction switch {
							Direction.Backwards => ReadReq.Types.Options.Types.ReadDirection.Backwards,
							Direction.Forwards => ReadReq.Types.Options.Types.ReadDirection.Forwards,
							_ => throw new InvalidOperationException()
						},
						ResolveLinks = resolveLinkTos,
						All = ReadReq.Types.Options.Types.AllOptions.FromPosition(position),
						Count = (ulong)maxCount,
					}
				},
				operationOptions,
				userCredentials,
				cancellationToken)) {
				if (confirmation != SubscriptionConfirmation.None) {
					continue;
				}

				yield return resolvedEvent;
			}
		}

		/// <summary>
		/// Asynchronously reads all events.
		/// </summary>
		/// <param name="direction">The <see cref="Direction"/> in which to read.</param>
		/// <param name="position">The <see cref="Position"/> to start reading from.</param>
		/// <param name="maxCount">The maximum count to read.</param>
		/// <param name="configureOperationOptions">An <see cref="Action{EventStoreClientOperationOptions}"/> to configure the operation's options.</param>
		/// <param name="resolveLinkTos">Whether to resolve LinkTo events automatically.</param>
		/// <param name="userCredentials">The optional <see cref="UserCredentials"/> to perform operation with.</param>
		/// <param name="cancellationToken">The optional <see cref="System.Threading.CancellationToken"/>.</param>
		/// <returns></returns>
		public IAsyncEnumerable<ResolvedEvent> ReadAllAsync(
			Direction direction,
			Position position,
			long maxCount = long.MaxValue,
			Action<EventStoreClientOperationOptions>? configureOperationOptions = null,
			bool resolveLinkTos = false,
			UserCredentials? userCredentials = null,
			CancellationToken cancellationToken = default) {
			var operationOptions = Settings.OperationOptions.Clone();
			configureOperationOptions?.Invoke(operationOptions);

			return ReadAllAsync(direction, position, maxCount, operationOptions, resolveLinkTos, userCredentials,
				cancellationToken);
		}

		private ReadStreamResult ReadStreamAsync(
			Direction direction,
			string streamName,
			StreamPosition revision,
			long maxCount,
			EventStoreClientOperationOptions operationOptions,
			bool resolveLinkTos = false,
			UserCredentials? userCredentials = null,
			CancellationToken cancellationToken = default) => new ReadStreamResult(_client, new ReadReq {
				Options = new ReadReq.Types.Options {
					ReadDirection = direction switch {
						Direction.Backwards => ReadReq.Types.Options.Types.ReadDirection.Backwards,
						Direction.Forwards => ReadReq.Types.Options.Types.ReadDirection.Forwards,
						_ => throw new InvalidOperationException()
					},
					ResolveLinks = resolveLinkTos,
					Stream = ReadReq.Types.Options.Types.StreamOptions.FromStreamNameAndRevision(streamName, revision),
					Count = (ulong)maxCount
				}
			},
			Settings,
			operationOptions,
			userCredentials,
			cancellationToken);

		/// <summary>
		/// Asynchronously reads all the events from a stream.
		///
		/// The result could also be inspected as a means to avoid handling exceptions as the <see cref="ReadState"/> would indicate whether or not the stream is readable./>
		/// </summary>
		/// <param name="direction">The <see cref="Direction"/> in which to read.</param>
		/// <param name="streamName">The name of the stream to read.</param>
		/// <param name="revision">The <see cref="StreamRevision"/> to start reading from.</param>
		/// <param name="maxCount">The number of events to read from the stream.</param>
		/// <param name="configureOperationOptions">An <see cref="Action{EventStoreClientOperationOptions}"/> to configure the operation's options.</param>
		/// <param name="resolveLinkTos">Whether to resolve LinkTo events automatically.</param>
		/// <param name="userCredentials">The optional <see cref="UserCredentials"/> to perform operation with.</param>
		/// <param name="cancellationToken">The optional <see cref="System.Threading.CancellationToken"/>.</param>
		/// <returns></returns>
		public ReadStreamResult ReadStreamAsync(
			Direction direction,
			string streamName,
			StreamPosition revision,
			long maxCount = long.MaxValue,
			Action<EventStoreClientOperationOptions>? configureOperationOptions = null,
			bool resolveLinkTos = false,
			UserCredentials? userCredentials = null,
			CancellationToken cancellationToken = default) {
			var operationOptions = Settings.OperationOptions.Clone();
			configureOperationOptions?.Invoke(operationOptions);

			return ReadStreamAsync(direction, streamName, revision, maxCount, operationOptions, resolveLinkTos,
				userCredentials, cancellationToken);
		}

		/// <summary>
		/// A class that represents the result of a read operation.
		/// </summary>
		public class ReadStreamResult : IAsyncEnumerable<ResolvedEvent>, IAsyncEnumerator<ResolvedEvent> {
			private readonly IAsyncEnumerator<ReadResp> _call;
			private bool _moved;
			private CancellationToken _cancellationToken;
			private readonly string _streamName;

			internal ReadStreamResult(
				Streams.Streams.StreamsClient client,
				ReadReq request,
				EventStoreClientSettings settings,
				EventStoreClientOperationOptions operationOptions,
				UserCredentials? userCredentials, CancellationToken cancellationToken) {
				if (request.Options.CountOptionCase == ReadReq.Types.Options.CountOptionOneofCase.Count &&
				    request.Options.Count <= 0) {
					throw new ArgumentOutOfRangeException("count");
				}

				_streamName = request.Options.Stream.StreamIdentifier;

				if (request.Options.Filter == null) {
					request.Options.NoFilter = new Empty();
				}

				request.Options.UuidOption = new ReadReq.Types.Options.Types.UUIDOption {Structured = new Empty()};
				_moved = false;
				_call = client.Read(request,
						EventStoreCallOptions.Create(settings, operationOptions, userCredentials, cancellationToken))
					.ResponseStream.ReadAllAsync().GetAsyncEnumerator();

				ReadState = GetStateInternal();

				async Task<ReadState> GetStateInternal() {
					_moved = await _call.MoveNextAsync(cancellationToken).ConfigureAwait(false);
					return _call.Current?.ContentCase switch {
						ReadResp.ContentOneofCase.StreamNotFound => Client.ReadState.StreamNotFound,
						_ => Client.ReadState.Ok
					};
				}

				Current = default;
			}

			/// <summary>
			/// The <see cref="ReadState"/>.
			/// </summary>
			public Task<ReadState> ReadState { get; }

			/// <inheritdoc />
			public IAsyncEnumerator<ResolvedEvent> GetAsyncEnumerator(
				CancellationToken cancellationToken = new CancellationToken()) {
				_cancellationToken = cancellationToken;
				return this;
			}

			/// <inheritdoc />
			public ValueTask DisposeAsync() => _call.DisposeAsync();

			/// <inheritdoc />
			public async ValueTask<bool> MoveNextAsync() {
				var state = await ReadState.ConfigureAwait(false);
				if (state != Client.ReadState.Ok) {
					throw ExceptionFromState(state, _streamName);
				}

				if (_moved) {
					_moved = false;
					if (IsCurrentItemEvent()) return true;
				}

				while (await _call.MoveNextAsync(_cancellationToken).ConfigureAwait(false)) {
					if (IsCurrentItemEvent()) return true;
				}

				Current = default;
				return false;

				bool IsCurrentItemEvent() {
					var (confirmation, position, @event) = ConvertToItem(_call.Current);
					if (confirmation == SubscriptionConfirmation.None && position == null) {
						Current = @event;
						return true;
					}

					return false;
				}
			}

			private static Exception ExceptionFromState(ReadState state, string streamName) {
				return state switch {
					Client.ReadState.StreamNotFound => new StreamNotFoundException(streamName),
					_ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
				};
			}

			/// <inheritdoc />
			public ResolvedEvent Current { get; private set; }
		}


		private async IAsyncEnumerable<(SubscriptionConfirmation, Position?, ResolvedEvent)> ReadInternal(
			ReadReq request,
			EventStoreClientOperationOptions operationOptions,
			UserCredentials? userCredentials,
			[EnumeratorCancellation] CancellationToken cancellationToken) {
			if (request.Options.CountOptionCase == ReadReq.Types.Options.CountOptionOneofCase.Count &&
			    request.Options.Count <= 0) {
				throw new ArgumentOutOfRangeException("count");
			}

			if (request.Options.Filter == null) {
				request.Options.NoFilter = new Empty();
			}

			request.Options.UuidOption = new ReadReq.Types.Options.Types.UUIDOption {Structured = new Empty()};

			using var call = _client.Read(request,
				EventStoreCallOptions.Create(Settings, operationOptions, userCredentials, cancellationToken));

			await foreach (var e in call.ResponseStream
				.ReadAllAsync(cancellationToken)
				.Select(ConvertToItem)
				.WithCancellation(cancellationToken)
				.ConfigureAwait(false)) {
				yield return e;
			}
		}

		private static (SubscriptionConfirmation, Position?, ResolvedEvent) ConvertToItem(ReadResp response) =>
			response.ContentCase switch {
				ReadResp.ContentOneofCase.Confirmation => (
					new SubscriptionConfirmation(response.Confirmation.SubscriptionId), null, default),
				ReadResp.ContentOneofCase.Event => (SubscriptionConfirmation.None,
					null,
					ConvertToResolvedEvent(response.Event)),
				ReadResp.ContentOneofCase.Checkpoint => (SubscriptionConfirmation.None,
					new Position(response.Checkpoint.CommitPosition, response.Checkpoint.PreparePosition),
					default),
				_ => throw new InvalidOperationException()
			};

		private static ResolvedEvent ConvertToResolvedEvent(ReadResp.Types.ReadEvent readEvent) =>
			new ResolvedEvent(
				ConvertToEventRecord(readEvent.Event)!,
				ConvertToEventRecord(readEvent.Link),
				readEvent.PositionCase switch {
					ReadResp.Types.ReadEvent.PositionOneofCase.CommitPosition => readEvent.CommitPosition,
					ReadResp.Types.ReadEvent.PositionOneofCase.NoPosition => null,
					_ => throw new InvalidOperationException()
				});

		private static EventRecord? ConvertToEventRecord(ReadResp.Types.ReadEvent.Types.RecordedEvent e) =>
			e == null
				? null
				: new EventRecord(
					e.StreamIdentifier,
					Uuid.FromDto(e.Id),
					new StreamPosition(e.StreamRevision),
					new Position(e.CommitPosition, e.PreparePosition),
					e.Metadata,
					e.Data.ToByteArray(),
					e.CustomMetadata.ToByteArray());
	}
}
