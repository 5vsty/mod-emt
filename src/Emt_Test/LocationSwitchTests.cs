using FSDEV.MOD.Emt.Emt.Domain;
using FSDEV.MOD.Emt.Emt.Validation;
using FSDEV.MOD.Emt.Emt_Server.Errors;

namespace FSDEV.MOD.Emt.Emt_Test;

/// <summary>
/// Config switch Location.Enabled: false = the facility structure is not read and a report needs no location.
/// Default true keeps the approved behavior (BR-19, FR-08).
/// </summary>
[TestClass]
public class LocationSwitchTests
{
    private static ErrorService WithoutLocation(TestEnvironment env)
        => new(env.Store, env.Index, env.Sequence, env.Catalog, env.Locations, env.Permissions, env.Clock, locationEnabled: false);

    [TestMethod]
    public void Submit_without_location_works_and_never_reads_the_structure_when_the_switch_is_off()
    {
        var env = new TestEnvironment();
        env.LocationSource.Available = false;
        var callsBefore = env.LocationSource.Calls;

        var result = WithoutLocation(env).Submit(TestEnvironment.Anna, Guid.NewGuid(), null, env.ScanCode, "Scanner does not read the code");

        Assert.IsTrue(result.Successful, result.Reason);
        Assert.AreEqual(string.Empty, result.Value!.Error.LocationId);
        Assert.AreEqual(string.Empty, result.Value.Error.LocationPath);
        Assert.AreEqual(callsBefore, env.LocationSource.Calls, "the structure must not be read");
        Assert.IsTrue(env.Store.Records.ContainsKey(result.Value.Error.Id), "written to the store");
    }

    [TestMethod]
    public void Report_form_data_has_no_locations_and_says_so_when_the_switch_is_off()
    {
        var env = new TestEnvironment();
        env.LocationSource.Available = false;
        var callsBefore = env.LocationSource.Calls;

        var result = WithoutLocation(env).GetReportFormData(TestEnvironment.Anna);

        Assert.IsTrue(result.Successful, result.Reason);
        Assert.IsFalse(result.Value!.LocationEnabled);
        Assert.AreEqual(0, result.Value.Locations.Count);
        Assert.AreEqual(callsBefore, env.LocationSource.Calls);
    }

    [TestMethod]
    public void Location_stays_mandatory_by_default()
    {
        var env = new TestEnvironment();

        var result = env.Errors.Submit(TestEnvironment.Anna, Guid.NewGuid(), null, env.ScanCode, "x");

        Assert.IsFalse(result.Successful);
        Assert.IsTrue(result.ValidationErrors.Any(error => error.Field == "LocationId" && error.MessageKey == ValidationKeys.REQUIRED));
        Assert.IsTrue(env.Errors.GetReportFormData(TestEnvironment.Anna).Value!.LocationEnabled);
    }

    [TestMethod]
    public void Validator_only_requires_the_location_when_asked_to()
    {
        var required = ErrorReportValidator.Validate(null, Guid.NewGuid(), false, "x");
        var notRequired = ErrorReportValidator.Validate(null, Guid.NewGuid(), false, "x", locationRequired: false);

        Assert.IsTrue(required.Any(error => error.Field == "LocationId"));
        Assert.IsFalse(notRequired.Any(error => error.Field == "LocationId"));
    }
}
