﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace CacheTower.Extensions.Redis
{
	/// <summary>
	/// The <see cref="RedisRemoteEvictionExtension"/> broadcasts cache updates, evictions and flushes to Redis to allow for remote eviction of old cache data.
	/// When one of these events is received, it will perform that action locally to the configured cache layers.
	/// </summary>
	public class RedisRemoteEvictionExtension : ICacheChangeExtension
	{
		private ISubscriber Subscriber { get; }
		private string FlushChannel { get; }
		private string EvictionChannel { get; }

		private bool IsRegistered { get; set; }

		private readonly object LockObj = new object();
		private HashSet<string> FlaggedEvictions { get; }
		private bool HasFlushTriggered { get; set; }
		private ICacheLayer[] EvictFromLayers { get; }

		/// <summary>
		/// Creates a new instance of <see cref="RedisRemoteEvictionExtension"/>.
		/// </summary>
		/// <param name="connection">The primary connection to the Redis instance where the messages will be broadcast and received through.</param>
		/// <param name="evictFromLayers">The cache layers to either evict or flush when a message is received from Redis.</param>
		/// <param name="channelPrefix">The channel prefix to use for the Redis communication.</param>
		public RedisRemoteEvictionExtension(IConnectionMultiplexer connection, ICacheLayer[] evictFromLayers, string channelPrefix = "CacheTower")
		{
			if (connection == null)
			{
				throw new ArgumentNullException(nameof(connection));
			}

			if (channelPrefix == null)
			{
				throw new ArgumentNullException(nameof(channelPrefix));
			}

			Subscriber = connection.GetSubscriber();
			FlushChannel = $"{channelPrefix}.RemoteFlush";
			EvictionChannel = $"{channelPrefix}.RemoteEviction";
			FlaggedEvictions = new HashSet<string>(StringComparer.Ordinal);
			EvictFromLayers = evictFromLayers ?? throw new ArgumentNullException(nameof(evictFromLayers));
		}

		/// <remarks>
		/// This will broadcast to Redis that the cache entry belonging to <paramref name="cacheKey"/> is now out-of-date and should be evicted.
		/// </remarks>
		/// <inheritdoc/>
		public ValueTask OnCacheUpdateAsync(string cacheKey, DateTime expiry, CacheUpdateType cacheUpdateType)
		{
			if (cacheUpdateType == CacheUpdateType.AddOrUpdateEntry)
			{
				return FlagEvictionAsync(cacheKey);
			}
			return default;
		}
		/// <remarks>
		/// This will broadcast to Redis that the cache entry belonging to <paramref name="cacheKey"/> is to be evicted.
		/// </remarks>
		/// <inheritdoc/>
		public ValueTask OnCacheEvictionAsync(string cacheKey)
		{
			return FlagEvictionAsync(cacheKey);
		}

		private async ValueTask FlagEvictionAsync(string cacheKey)
		{
			lock (LockObj)
			{
				FlaggedEvictions.Add(cacheKey);
			}

			await Subscriber.PublishAsync(EvictionChannel, cacheKey, CommandFlags.FireAndForget);
		}

		/// <remarks>
		/// This will broadcast to Redis that the cache should be flushed.
		/// </remarks>
		/// <inheritdoc/>
		public async ValueTask OnCacheFlushAsync()
		{
			lock (LockObj)
			{
				HasFlushTriggered = true;
			}

			await Subscriber.PublishAsync(FlushChannel, RedisValue.EmptyString, CommandFlags.FireAndForget);
		}

		/// <inheritdoc/>
		public void Register(ICacheStack cacheStack)
		{
			if (IsRegistered)
			{
				throw new InvalidOperationException($"{nameof(RedisRemoteEvictionExtension)} can only be registered to one {nameof(ICacheStack)}");
			}
			IsRegistered = true;

			Subscriber.Subscribe(EvictionChannel)
				.OnMessage(async (channelMessage) =>
				{
					string cacheKey = channelMessage.Message;

					var shouldEvictLocally = false;
					lock (LockObj)
					{
						shouldEvictLocally = FlaggedEvictions.Remove(cacheKey) == false;
					}

					if (shouldEvictLocally)
					{
						for (var i = 0; i < EvictFromLayers.Length; i++)
						{
							await EvictFromLayers[i].EvictAsync(cacheKey);
						}
					}
				});

			Subscriber.Subscribe(FlushChannel)
				.OnMessage(async (channelMessage) =>
				{
					var shouldFlushLocally = false;
					lock (LockObj)
					{
						shouldFlushLocally = !HasFlushTriggered;
						HasFlushTriggered = false;
					}

					if (shouldFlushLocally)
					{
						for (var i = 0; i < EvictFromLayers.Length; i++)
						{
							await EvictFromLayers[i].FlushAsync();
						}
					}
				});
		}
	}
}
