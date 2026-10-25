using ImmersiveSBS.Depth;

namespace ImmersiveSBS.Tests;

/// <summary>
/// Video Depth Anything's streaming window (<see cref="StateWindow"/>) against the authors'
/// infer_video_depth_one (video_depth_anything/video_depth_stream.py), its list handling written
/// out here as they wrote it: every step must read the states of the same frames, in the same
/// order, and no slot may be overwritten while the window still holds it.
/// </summary>
public class StateWindowTests
{
    private const int InferLen = 32, Gap = 41;

    [Fact]
    public void EveryStepReadsTheFramesTheAuthorsRead()
    {
        var window = new StateWindow();
        var frameInSlot = new int[StateWindow.Slots];
        List<int> cacheList = [];   // the authors' frame_cache_list, as the frame each entry is from
        int id = -1;

        void Frame()
        {
            id++;
            if (id > 0)
            {
                // cur_list = frame_cache_list[0:2] + frame_cache_list[-INFER_LEN+3:]
                List<int> expected = [.. cacheList.Take(2), .. cacheList.Skip(cacheList.Count - (InferLen - 3))];
                Assert.Equal(InferLen - 1, expected.Count);
                int[] read = [.. Enumerable.Range(0, StateWindow.Past).Select(k => frameInSlot[window.Read(k)])];
                Assert.Equal(expected, read);
            }
            int slot = window.FreeSlot;
            Assert.InRange(slot, 0, StateWindow.Slots - 1);
            frameInSlot[slot] = id;
            window.Add(slot);

            if (id == 0) cacheList = [.. Enumerable.Repeat(0, InferLen)];
            else cacheList.Add(id);
            if (id + InferLen > Gap + 1) cacheList.RemoveAt(1);
        }

        for (int i = 0; i < 150; i++) Frame();

        // A seek or a scene cut: the next frame is a stream's first again.
        window.Reset();
        Assert.True(window.IsEmpty);
        id = -1;
        cacheList = [];
        for (int i = 0; i < 60; i++) Frame();
    }
}
