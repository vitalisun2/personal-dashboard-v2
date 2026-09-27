using PersonalDashboard.V2.Planning.Domain;
using Xunit;

public sealed class PlanningProgressTests
{
    [Fact]
    public void EpicsHaveEqualWeightDespiteDifferentFeatureCounts()
    {
        var project = new Project("Project");
        AddEpic(project, 2, 2);
        AddEpic(project, 8, 0);

        Assert.Equal(50, project.ProgressPercent);
    }

    [Theory]
    [InlineData(2, 2, 67)]
    [InlineData(1, 1, 33)]
    [InlineData(2, 1, 50)]
    public void PartialEpicProgressContributesToProject(int firstDone, int secondDone, int expected)
    {
        var project = new Project("Project");
        AddEpic(project, 2, firstDone);
        AddEpic(project, 2, secondDone);
        AddEpic(project, 2, 0);

        Assert.Equal(expected, project.ProgressPercent);
    }

    [Fact]
    public void EmptyEpicCountsAsNotStartedAndRemovingItRecalculatesProgress()
    {
        var project = new Project("Project");
        Assert.Equal(0, project.ProgressPercent);
        var empty = project.AddMilestone("Empty epic");
        Assert.Equal(0, empty.ProgressPercent);
        AddEpic(project, 1, 1);
        Assert.Equal(50, project.ProgressPercent);

        project.RemoveMilestone(empty.Id);
        Assert.Equal(100, project.ProgressPercent);
    }

    [Fact]
    public void FeatureStatusChangesRecalculateEpicAndProject()
    {
        var project = new Project("Project");
        var epic = project.AddMilestone("Epic");
        var feature = epic.AddFeature("Feature");
        feature.SetStatus(FeatureStatus.Active);
        Assert.Equal(0, project.ProgressPercent);
        feature.SetStatus(FeatureStatus.Done);
        Assert.Equal(100, epic.ProgressPercent);
        Assert.Equal(100, project.ProgressPercent);
        feature.SetStatus(FeatureStatus.Planned);
        Assert.Equal(0, project.ProgressPercent);
    }

    private static void AddEpic(Project project, int count, int done)
    {
        var epic = project.AddMilestone("Epic");
        for (var i = 0; i < count; i++)
        {
            var feature = epic.AddFeature($"Feature {i}");
            if (i < done) feature.SetStatus(FeatureStatus.Done);
        }
    }
}
