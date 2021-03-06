using System.Threading.Tasks;

namespace NEStore
{
	public interface IDispatcher<T>
	{
		/// <summary>
		/// Dispatch a commit
		/// </summary>
		/// /// <param name="bucketName">Bucket identifier</param>
		/// <param name="commit">Commit to dispatch</param>
		Task DispatchAsync(string bucketName, CommitData<T> commit);
	}
}