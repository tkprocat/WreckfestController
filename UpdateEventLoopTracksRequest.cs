using WreckfestController.Models;

namespace WreckfestController
{
    public class UpdateEventLoopTracksRequest
    {
        public string CollectionName { get; set; } = string.Empty;
        public List<EventLoopTrack> Tracks { get; set; }
        public UpdateEventLoopTracksRequest(string collectionName, List<EventLoopTrack> tracks)
        {
            CollectionName = collectionName;
            Tracks = tracks;
        }
    }
}
