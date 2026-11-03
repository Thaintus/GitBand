using GitBinder.Domain.Accounts;
using GitBinder.Domain.Projects;
using GitBinder.Domain.Services;

namespace GitBinder.Tests;

public sealed class AccountDisplaySnapshotTests
{
    [Fact]
    public void DisplaySnapshot_ReusesStateButAcceptsFreshBindingForEachProject()
    {
        var state = new CountingState();
        var forbidden = new ForbiddenReaders();
        var fallback = new Account { IsDefault = true };
        var first = new Account();
        var second = new Account();
        var disabled = new Account { Enabled = false };
        var snapshot = new EffectiveAccountResolver(state, forbidden, forbidden)
            .CaptureDisplaySnapshot([fallback, first, second, disabled]);
        var project = new Project();
        for (var i = 0; i < 100; i++)
        {
            Assert.Same(first, snapshot.Resolve(project, first.Id).Account);
            Assert.Same(second, snapshot.Resolve(project, second.Id).Account);
            Assert.Same(fallback, snapshot.Resolve(project, disabled.Id).Account);
            Assert.Same(fallback, snapshot.Resolve(project, null).Account);
        }
        Assert.Equal(EffectiveReason.Binding, snapshot.Resolve(project, disabled.Id).Reason);
        Assert.Equal(EffectiveReason.Default, snapshot.Resolve(project, null).Reason);
        Assert.Equal(1, state.EnabledReads);
        Assert.Equal(0, state.AccountReads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DisplaySnapshot_GlobalModeHasPriorityAndDisabledGlobalFallsBack(bool globalEnabled)
    {
        var global = new Account { Enabled = globalEnabled };
        var fallback = new Account { IsDefault = true };
        var bound = new Account();
        var state = new CountingState { Enabled = true, AccountId = global.Id };
        var forbidden = new ForbiddenReaders();
        var snapshot = new EffectiveAccountResolver(state, forbidden, forbidden)
            .CaptureDisplaySnapshot([global, fallback, bound]);
        state.Enabled = false;
        var result = snapshot.Resolve(new Project(), bound.Id);
        Assert.Same(globalEnabled ? global : fallback, result.Account);
        Assert.Equal(EffectiveReason.GlobalMode, result.Reason);
        Assert.Equal(1, state.EnabledReads);
        Assert.Equal(1, state.AccountReads);
    }

    private sealed class CountingState : IGlobalModeState
    {
        public bool Enabled { get; set; }
        public Guid? AccountId { get; set; }
        public int EnabledReads { get; private set; }
        public int AccountReads { get; private set; }
        public bool IsEnabled { get { EnabledReads++; return Enabled; } }
        public Guid? GlobalAccountId { get { AccountReads++; return AccountId; } }
    }

    private sealed class ForbiddenReaders : IAccountReader, IBindingReader
    {
        public Account? GetById(Guid id) => throw new InvalidOperationException("Unexpected per-row account read");
        public Account? GetDefault() => throw new InvalidOperationException("Unexpected per-row default read");
        public Guid? GetAccountIdByProject(Guid id) => throw new InvalidOperationException("Unexpected per-row binding read");
    }
}
