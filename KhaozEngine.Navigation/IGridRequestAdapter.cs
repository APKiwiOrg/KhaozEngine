using GridSearchRequest = KhaozEngine.Navigation.GridPathPlanner.GridSearchRequest;
using RequestAdmission = KhaozEngine.Navigation.GridPathPlanner.GridSearchRequest.GridPlanningWorkspaces.RequestAdmission;

namespace KhaozEngine.Navigation;

/// <summary>Provides the grid and budgeted stages for one adapter instance's request pool.</summary>
internal interface IGridRequestAdapter
{
    /// <summary>The grid that supplies the adapter's search and workspace sizing.</summary>
    GridPathPlanner RequestGrid { get; }

    /// <summary>Creates a pending handle without acquiring a lease or applying begin effects.</summary>
    GridSearchRequest CreateRequest(RequestAdmission admission);
}
