using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Driver;

namespace NEStore.MongoDb
{
	public class MongoDbBucket : IBucket
	{
		private bool _indexesEnsured;
		private readonly MongoDbEventStore _eventStore;
		public IMongoCollection<CommitData> Collection { get; }
		public string BucketName { get; }

		public MongoDbBucket(MongoDbEventStore eventStore, string bucketName)
		{
			_eventStore = eventStore;
			Collection = eventStore.CollectionFromBucket(bucketName);
			BucketName = bucketName;
		}

		public async Task<WriteResult> WriteAsync(Guid streamId, int expectedStreamRevision, IEnumerable<object> events)
		{
			if (expectedStreamRevision < 0)
				throw new ArgumentOutOfRangeException(nameof(expectedStreamRevision));

			if (_eventStore.CheckStreamRevisionBeforeWriting)
			{
				// Note: this check doesn't ensure that in case of real concurrency no one can insert the same commit
				//  the real check is done via a mongo index "StreamRevision". This check basically just ensure to do not write 
				//  revision with holes
				var actualRevision = await GetStreamRevisionAsync(streamId)
																						.ConfigureAwait(false);
				if (actualRevision > expectedStreamRevision)
					throw new ConcurrencyWriteException("Someone else is working on the same bucket or stream");
				if (actualRevision < expectedStreamRevision) // Ensure to write commits sequentially
					throw new ArgumentOutOfRangeException(nameof(expectedStreamRevision));
			}

			await AutoEnsureIndexesAsync()
				.ConfigureAwait(false);

			await CheckForUndispatchedAsync()
				.ConfigureAwait(false);

			var eventsArray = events.ToArray();

			var commit = await CreateCommitAsync(streamId, expectedStreamRevision, eventsArray)
				.ConfigureAwait(false);

			try
			{
				await Collection.InsertOneAsync(commit)
					.ConfigureAwait(false);
			}
			catch (MongoWriteException ex)
			{
				if (ex.WriteError != null && ex.WriteError.Code == 11000)
					throw new ConcurrencyWriteException("Someone else is working on the same bucket or stream", ex);
			}

			var dispatchTask = DispatchCommitAsync(commit);

			return new WriteResult(commit, dispatchTask);
		}

		public async Task DispatchUndispatchedAsync()
		{
			var commits = await Collection
				.Find(p => p.Dispatched == false)
				.Sort(Builders<CommitData>.Sort.Ascending(p => p.BucketRevision))
				.ToListAsync()
				.ConfigureAwait(false);

			foreach (var commit in commits)
				await DispatchCommitAsync(commit)
					.ConfigureAwait(false);
		}

		public Task RollbackAsync(long bucketRevision)
		{
			return Collection
				.DeleteManyAsync(p => p.BucketRevision > bucketRevision);
		}

		public async Task<IEnumerable<object>> GetEventsAsync(Guid? streamId = null, long? fromBucketRevision = null, long? toBucketRevision = null)
		{
			var commits = await GetCommitsAsync(streamId, fromBucketRevision, toBucketRevision)
				.ConfigureAwait(false);

			return commits.SelectMany(c => c.Events);
		}

		public async Task<IEnumerable<object>> GetEventsForStreamAsync(Guid streamId, int? fromStreamRevision = null, int? toStreamRevision = null)
		{
			if (fromStreamRevision <= 0)
				throw new ArgumentOutOfRangeException(nameof(fromStreamRevision),
						"Parameter must be greater than 0.");

			if (toStreamRevision <= 0)
				throw new ArgumentOutOfRangeException(nameof(toStreamRevision), "Parameter must be greater than 0.");

			var filter = Builders<CommitData>.Filter.Eq(p => p.StreamId, streamId);

			// Note: I cannot filter the start because I don't want to query mongo using non indexed fields
			//  and I assume that for one stream we should not have so many events ...
			if (toStreamRevision != null)
				filter = filter & Builders<CommitData>.Filter.Lt(p => p.StreamRevisionStart, toStreamRevision.Value);

			var commits = await Collection
				.Find(filter)
				.Sort(Builders<CommitData>.Sort.Ascending(p => p.BucketRevision))
				.ToListAsync()
				.ConfigureAwait(false);

			var events = commits
				.SelectMany(c => c.Events)
				.ToList();

			var safeFrom = fromStreamRevision ?? 1;
			var safeTo = toStreamRevision ?? events.Count;

			return events
				.Skip(safeFrom - 1)
				.Take(safeTo - safeFrom + 1)
				.ToList();
		}

		public async Task<bool> HasUndispatchedCommitsAsync()
		{
			return await Collection
				.Find(p => p.Dispatched == false)
				.AnyAsync()
				.ConfigureAwait(false);
		}

		public async Task<IEnumerable<CommitData>> GetCommitsAsync(Guid? streamId = null, long? fromBucketRevision = null, long? toBucketRevision = null)
		{
			if (fromBucketRevision <= 0)
				throw new ArgumentOutOfRangeException(nameof(fromBucketRevision),
						"Parameter must be greater than 0.");

			if (toBucketRevision <= 0)
				throw new ArgumentOutOfRangeException(nameof(toBucketRevision), "Parameter must be greater than 0.");

			var filter = Builders<CommitData>.Filter.Empty;
			if (streamId != null)
				filter = filter & Builders<CommitData>.Filter.Eq(p => p.StreamId, streamId.Value);

			if (fromBucketRevision != null)
				filter = filter & Builders<CommitData>.Filter.Gte(p => p.BucketRevision, fromBucketRevision.Value);
			if (toBucketRevision != null)
				filter = filter & Builders<CommitData>.Filter.Lte(p => p.BucketRevision, toBucketRevision.Value);

			var commits = await Collection
				.Find(filter)
				.Sort(Builders<CommitData>.Sort.Ascending(p => p.BucketRevision))
				.ToListAsync()
				.ConfigureAwait(false);

			return commits;
		}

		public async Task<long> GetBucketRevisionAsync()
		{
			var result = await Collection
				.Find(Builders<CommitData>.Filter.Empty)
				.Sort(Builders<CommitData>.Sort.Descending(p => p.BucketRevision))
				.FirstOrDefaultAsync()
				.ConfigureAwait(false);

			return result?.BucketRevision ?? 0;
		}

		public async Task<int> GetStreamRevisionAsync(Guid streamId, long? atBucketRevision = null)
		{
			var filter = Builders<CommitData>.Filter.Eq(p => p.StreamId, streamId);
			if (atBucketRevision != null && atBucketRevision != long.MaxValue)
				filter = filter & Builders<CommitData>.Filter.Lte(p => p.BucketRevision, atBucketRevision.Value);

			var result = await Collection
				.Find(filter)
				.Sort(Builders<CommitData>.Sort.Descending(p => p.BucketRevision))
				.FirstOrDefaultAsync()
				.ConfigureAwait(false);

			return result?.StreamRevisionEnd ?? 0;
		}

		public async Task<IEnumerable<Guid>> GetStreamIdsAsync(long? fromBucketRevision = null, long? toBucketRevision = null)
		{
			var filter = Builders<CommitData>.Filter.Empty;
			if (fromBucketRevision != null && fromBucketRevision != long.MinValue)
				filter = filter & Builders<CommitData>.Filter.Gte(p => p.BucketRevision, fromBucketRevision.Value);
			if (toBucketRevision != null && toBucketRevision != long.MaxValue)
				filter = filter & Builders<CommitData>.Filter.Lte(p => p.BucketRevision, toBucketRevision.Value);

			var cursor = await Collection
				.DistinctAsync(p => p.StreamId, filter)
				.ConfigureAwait(false);
			var result = await cursor.ToListAsync()
				.ConfigureAwait(false);

			return result;
		}


		private async Task<CommitData> CreateCommitAsync(Guid streamId, int expectedStreamRevision, object[] eventsArray)
		{
			var commit = new CommitData
			{
				BucketRevision = await GetBucketRevisionAsync().ConfigureAwait(false) + 1,
				Dispatched = false,
				Events = eventsArray,
				StreamId = streamId,
				StreamRevisionStart = expectedStreamRevision,
				StreamRevisionEnd = expectedStreamRevision + eventsArray.Length
			};
			return commit;
		}

		private async Task AutoEnsureIndexesAsync()
		{
			if (_indexesEnsured || !_eventStore.AutoEnsureIndexes)
				return;

			await _eventStore.EnsureBucketAsync(BucketName)
				.ConfigureAwait(false);
			_indexesEnsured = true;
		}

		private async Task DispatchCommitAsync(CommitData commit)
		{
			foreach (var e in commit.Events)
			{
				await Task.WhenAll(_eventStore.GetDispatchers().Select(x => x.DispatchAsync(e)))
					.ConfigureAwait(false);
			}

			await Collection.UpdateOneAsync(
				p => p.BucketRevision == commit.BucketRevision,
				Builders<CommitData>.Update.Set(p => p.Dispatched, true))
				.ConfigureAwait(false);
		}

		private async Task CheckForUndispatchedAsync()
		{
			if (!_eventStore.AutoCheckUndispatched)
				return;

			var hasUndispatched = await HasUndispatchedCommitsAsync()
				.ConfigureAwait(false);
			if (hasUndispatched)
				throw new UndispatchedEventsFoundException("Undispatched events found, cannot write new events");

			// Eventually here I can try to dispatch undispatched to try to "recover" from a broken situations...
		}

	}
}