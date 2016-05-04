﻿using System;
using System.Threading.Tasks;
using MongoDB.Driver;
using Xunit;

namespace StreamLedger.MongoDb.UnitTests
{
	public class MongoDbBucketManagerTests : IClassFixture<BucketManagerFixture>
	{
		private readonly BucketManagerFixture _fixture;

		public MongoDbBucketManagerTests(BucketManagerFixture fixture)
		{
			_fixture = fixture;
		}

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("test test")]
		[InlineData("Vsm")]
		[InlineData("test(")]
		[InlineData("5test")]
		public void Bucket_name_should_be_valid(string bucketName)
		{
			var target = _fixture.CreateTarget();

			Assert.ThrowsAsync<ArgumentException>(() => target.EnsureBucketAsync(bucketName)).Wait();
		}

		[Fact]
		public async Task EnsureBucket_create_required_collections()
		{
			await _fixture.Target.EnsureBucketAsync(_fixture.BucketName);

			var collections = await (await _fixture.Target.Database.ListCollectionsAsync()).ToListAsync();

			Assert.Contains(collections, p => p["name"] == $"{_fixture.BucketName}.commits");
		}
	}
}