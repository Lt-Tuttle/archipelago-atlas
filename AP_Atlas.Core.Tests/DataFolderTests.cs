namespace AP_Atlas.Core.Tests;

public class DataFolderTests
{
    [Fact]
    public void The_override_comes_first_then_the_portable_folder_when_it_can_be_written()
    {
        using var temp = new TempFolder();
        string program = temp.File("program"), local = temp.File("local");
        Directory.CreateDirectory(program);
        var overridden = DataFolder.Resolve(temp.File("elsewhere"), program, local, _ => null);
        Assert.Equal(DataFolder.Source.Override, overridden.Source);
        Assert.Equal(temp.File("elsewhere"), overridden.Path);

        var portable = DataFolder.Resolve(null, program, local, DataFolder.Probe);
        Assert.Equal(DataFolder.Source.Portable, portable.Source);
        Assert.Equal(Path.Combine(program, DataFolder.PortableName), portable.Path);
        Assert.Null(portable.Problem);
        Assert.True(Directory.Exists(portable.Path));
        Assert.Empty(Directory.GetFiles(portable.Path)); // the probe file is gone
        Assert.False(Directory.Exists(Path.Combine(local, DataFolder.AppFolderName))); // nothing outside Atlas's folder was touched
    }

    [Fact]
    public void A_program_folder_that_cant_be_written_needs_a_choice_unless_one_was_remembered()
    {
        using var temp = new TempFolder();
        string program = temp.File("program"), local = temp.File("local");
        Directory.CreateDirectory(program);
        // A file where the folder should be: the folder can't be made.
        File.WriteAllText(Path.Combine(program, DataFolder.PortableName), "in the way");

        var needs = DataFolder.Resolve(null, program, local, DataFolder.Probe);
        Assert.Equal(DataFolder.Source.NeedsChoice, needs.Source);
        Assert.Null(needs.Path);
        Assert.NotNull(needs.Problem);
        Assert.Equal(Path.Combine(program, DataFolder.PortableName), needs.Portable);

        Assert.Equal(Path.Combine(local, DataFolder.AppFolderName, DataFolder.PortableName), DataFolder.DefaultFallback(local));
        Assert.Null(DataFolder.ReadPointer(local));
        string chosen = temp.File("chosen");
        DataFolder.WritePointer(local, chosen);
        Assert.Equal(chosen, DataFolder.ReadPointer(local));
        Assert.Equal(Path.Combine(local, DataFolder.AppFolderName, DataFolder.PointerFile), DataFolder.PointerPath(local));

        var remembered = DataFolder.Resolve(null, program, local, DataFolder.Probe);
        Assert.Equal(DataFolder.Source.Chosen, remembered.Source);
        Assert.Equal(chosen, remembered.Path);
        Assert.Equal(needs.Problem, remembered.Problem);

        // A remembered folder that can't be written any more: ask again.
        File.WriteAllText(Path.Combine(local, DataFolder.AppFolderName, DataFolder.PointerFile), Path.Combine(program, DataFolder.PortableName, "deeper"));
        Assert.Equal(DataFolder.Source.NeedsChoice, DataFolder.Resolve(null, program, local, DataFolder.Probe).Source);
        // A pointer that isn't a full path is ignored.
        File.WriteAllText(Path.Combine(local, DataFolder.AppFolderName, DataFolder.PointerFile), "relative\\folder");
        Assert.Null(DataFolder.ReadPointer(local));
    }

    [Fact]
    public void The_probe_says_why_a_folder_cant_be_used()
    {
        using var temp = new TempFolder();
        Assert.Null(DataFolder.Probe(temp.File("fine")));
        File.WriteAllText(temp.File("taken"), "a file");
        Assert.NotNull(DataFolder.Probe(temp.File("taken")));
        Assert.NotNull(DataFolder.Probe(temp.File("taken") + "\\inside"));
        Assert.NotNull(DataFolder.Probe("::not a path::"));
    }
}
