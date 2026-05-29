using Chiron.Cds.Web.FhirClient;
using Chiron.Cds.Web.Mappers;
using FluentAssertions;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chiron.Cds.Web.IntegrationTests;

/// <summary>
/// Unit tests for <see cref="FhirToFactMapper"/>. Exercises the
/// allergy-projection branch matrix that the real-Cerner integration
/// test can only hit when the network is available — these run offline.
/// </summary>
public class FhirToFactMapperTests
{
    private readonly FhirToFactMapper _mapper = new(NullLogger<FhirToFactMapper>.Instance);

    private static Patient BuildPatient() => new()
    {
        Id = "p1",
        Gender = AdministrativeGender.Female,
        BirthDate = "1990-01-01",
    };

    private static AllergyIntolerance BuildAllergy(
        string? text = "Sulfa drugs",
        string? clinicalStatus = "active",
        string? verificationStatus = "confirmed",
        AllergyIntolerance.AllergyIntoleranceCriticality? criticality = null,
        string? reactionText = null) => new()
    {
        Code = text is null ? null : new CodeableConcept { Text = text },
        ClinicalStatus = clinicalStatus is null ? null : new CodeableConcept
        {
            Coding = new List<Coding> { new("http://terminology.hl7.org/CodeSystem/allergyintolerance-clinical", clinicalStatus) },
        },
        VerificationStatus = verificationStatus is null ? null : new CodeableConcept
        {
            Coding = new List<Coding> { new("http://terminology.hl7.org/CodeSystem/allergyintolerance-verification", verificationStatus) },
        },
        Criticality = criticality,
        Reaction = reactionText is null
            ? new List<AllergyIntolerance.ReactionComponent>()
            : new List<AllergyIntolerance.ReactionComponent>
            {
                new()
                {
                    Manifestation = new List<CodeableConcept> { new() { Text = reactionText } },
                },
            },
    };

    private static PatientChart BuildChart(params AllergyIntolerance[] allergies) => new(
        Patient: BuildPatient(),
        Conditions: Array.Empty<Condition>(),
        Observations: Array.Empty<Observation>(),
        MedicationRequests: Array.Empty<MedicationRequest>(),
        Allergies: allergies,
        Immunizations: Array.Empty<Immunization>(),
        Procedures: Array.Empty<Procedure>(),
        Encounter: null);

    private static PatientChart BuildChartWithImmunizations(params Immunization[] immunizations) => new(
        Patient: BuildPatient(),
        Conditions: Array.Empty<Condition>(),
        Observations: Array.Empty<Observation>(),
        MedicationRequests: Array.Empty<MedicationRequest>(),
        Allergies: Array.Empty<AllergyIntolerance>(),
        Immunizations: immunizations,
        Procedures: Array.Empty<Procedure>(),
        Encounter: null);

    private static PatientChart BuildChartWithProcedures(params Procedure[] procedures) => new(
        Patient: BuildPatient(),
        Conditions: Array.Empty<Condition>(),
        Observations: Array.Empty<Observation>(),
        MedicationRequests: Array.Empty<MedicationRequest>(),
        Allergies: Array.Empty<AllergyIntolerance>(),
        Immunizations: Array.Empty<Immunization>(),
        Procedures: procedures,
        Encounter: null);

    [Fact]
    public void Allergy_With_Active_Status_Is_Projected_Active()
    {
        var inputs = _mapper.Project(BuildChart(BuildAllergy(text: "Ibuprofen")));
        inputs.Allergies.Should().ContainSingle().Which.Active.Should().BeTrue();
        inputs.Allergies[0].Substance.Should().Be("ibuprofen");
    }

    [Fact]
    public void Allergy_With_Inactive_Clinical_Status_Becomes_Inactive()
    {
        var inputs = _mapper.Project(BuildChart(
            BuildAllergy(text: "Penicillin", clinicalStatus: "inactive")));
        inputs.Allergies.Should().ContainSingle().Which.Active.Should().BeFalse();
    }

    [Fact]
    public void Refuted_Allergy_Is_Dropped()
    {
        var inputs = _mapper.Project(BuildChart(
            BuildAllergy(text: "Codeine", verificationStatus: "refuted")));
        inputs.Allergies.Should().BeEmpty();
    }

    [Fact]
    public void Entered_In_Error_Allergy_Is_Dropped()
    {
        var inputs = _mapper.Project(BuildChart(
            BuildAllergy(text: "Aspirin", verificationStatus: "entered-in-error")));
        inputs.Allergies.Should().BeEmpty();
    }

    [Fact]
    public void High_Criticality_Becomes_Critical()
    {
        var inputs = _mapper.Project(BuildChart(BuildAllergy(
            text: "Penicillin",
            criticality: AllergyIntolerance.AllergyIntoleranceCriticality.High)));
        inputs.Allergies.Should().ContainSingle().Which.Critical.Should().BeTrue();
    }

    [Fact]
    public void Low_Criticality_Is_Not_Critical()
    {
        var inputs = _mapper.Project(BuildChart(BuildAllergy(
            text: "Bee stings",
            criticality: AllergyIntolerance.AllergyIntoleranceCriticality.Low)));
        inputs.Allergies.Should().ContainSingle().Which.Critical.Should().BeFalse();
    }

    [Fact]
    public void Reaction_Text_Is_Captured()
    {
        var inputs = _mapper.Project(BuildChart(BuildAllergy(
            text: "Penicillin",
            reactionText: "Anaphylaxis")));
        inputs.Allergies.Should().ContainSingle().Which.Reaction.Should().Be("Anaphylaxis");
    }

    [Fact]
    public void Falls_Back_To_Coding_Display_When_No_Text()
    {
        var allergy = new AllergyIntolerance
        {
            Code = new CodeableConcept
            {
                Coding = new List<Coding> { new("http://snomed.info/sct", "294505008", "Sulfonamide allergy") },
            },
            ClinicalStatus = new CodeableConcept
            {
                Coding = new List<Coding> { new("", "active") },
            },
        };
        var inputs = _mapper.Project(BuildChart(allergy));
        inputs.Allergies.Should().ContainSingle().Which.Substance.Should().Be("sulfonamide");
    }

    [Fact]
    public void Allergy_With_Null_Code_Is_Dropped()
    {
        var inputs = _mapper.Project(BuildChart(BuildAllergy(text: null)));
        inputs.Allergies.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Penicillin G Sodium", "penicillin")]
    [InlineData("amoxicillin/clavulanate", "amoxicillin")]
    [InlineData("  IBUPROFEN  ", "ibuprofen")]
    [InlineData("Sulfa drugs", "sulfa")]
    [InlineData("Bactrim DS", "bactrim")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void NormalizeSubstance_Reduces_To_Canonical_Identifier(string? input, string expected)
    {
        FhirToFactMapper.NormalizeSubstance(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("Penicillin]<script>alert(1)</script>", "penicillin")]
    [InlineData("**bold** text", "bold")]
    [InlineData("[link](javascript:evil)", "link")]
    [InlineData("***", "")]
    public void NormalizeSubstance_Strips_Markdown_And_Html_Injection(string input, string expected)
    {
        FhirToFactMapper.NormalizeSubstance(input).Should().Be(expected,
            because: "the substance must not carry markdown/HTML special chars into card rendering");
    }

    [Theory]
    [InlineData("penicillin", "penicillin")]
    [InlineData("amoxicillin", "penicillin")]
    [InlineData("cephalexin", "cephalosporin")]
    [InlineData("ibuprofen", "nsaid")]
    [InlineData("atorvastatin", "statin")]
    [InlineData("lisinopril", "ace_inhibitor")]
    [InlineData("metformin", null)]
    [InlineData("unknown_drug", null)]
    public void ClassifyAllergen_Returns_Expected_Class(string substance, string? expected)
    {
        FhirToFactMapper.ClassifyAllergen(substance).Should().Be(expected);
    }

    // -------- Immunization projection --------

    private static Hl7.Fhir.Model.Immunization BuildImmunization(
        string? cvxCode = "140",
        string? displayText = null,
        string? occurrenceIso = "2025-10-15",
        Hl7.Fhir.Model.Immunization.ImmunizationStatusCodes status = Hl7.Fhir.Model.Immunization.ImmunizationStatusCodes.Completed)
    {
        var imm = new Hl7.Fhir.Model.Immunization { Status = status };
        if (cvxCode is not null || displayText is not null)
        {
            imm.VaccineCode = new CodeableConcept
            {
                Coding = cvxCode is null ? null : new List<Coding> { new("http://hl7.org/fhir/sid/cvx", cvxCode) },
                Text = displayText,
            };
        }
        if (occurrenceIso is not null)
        {
            imm.Occurrence = new FhirDateTime(occurrenceIso);
        }
        return imm;
    }

    [Fact]
    public void Immunization_Cvx_Code_Maps_To_Influenza()
    {
        var inputs = _mapper.Project(BuildChartWithImmunizations(BuildImmunization(cvxCode: "140")));
        inputs.Immunizations.Should().ContainSingle().Which.Vaccine.Should().Be("influenza");
    }

    [Fact]
    public void Immunization_Falls_Back_To_Display_Text_When_No_Cvx()
    {
        var imm = BuildImmunization(cvxCode: null, displayText: "Influenza, seasonal");
        var inputs = _mapper.Project(BuildChartWithImmunizations(imm));
        inputs.Immunizations.Should().ContainSingle().Which.Vaccine.Should().Be("influenza");
    }

    [Fact]
    public void Immunization_With_Unknown_Code_Is_Dropped()
    {
        var imm = BuildImmunization(cvxCode: "999999", displayText: "Some random vaccine");
        var inputs = _mapper.Project(BuildChartWithImmunizations(imm));
        inputs.Immunizations.Should().BeEmpty();
    }

    [Fact]
    public void Immunization_Missing_Occurrence_Date_Is_Dropped()
    {
        var inputs = _mapper.Project(BuildChartWithImmunizations(BuildImmunization(occurrenceIso: null)));
        inputs.Immunizations.Should().BeEmpty();
    }

    [Fact]
    public void Immunization_Status_Is_Captured()
    {
        var imm = BuildImmunization(status: Hl7.Fhir.Model.Immunization.ImmunizationStatusCodes.NotDone);
        var inputs = _mapper.Project(BuildChartWithImmunizations(imm));
        inputs.Immunizations.Should().ContainSingle().Which.Status.Should().Be("not-done");
    }

    [Fact]
    public void Immunization_Entered_In_Error_Is_Captured_Then_Filtered_By_Engine()
    {
        // The mapper preserves entered-in-error so an override-log audit
        // can still see the chart-level event; the engine's
        // LatestImmunization filter is responsible for excluding it from
        // coverage computation. Asserting the mapper side here.
        var imm = BuildImmunization(status: Hl7.Fhir.Model.Immunization.ImmunizationStatusCodes.EnteredInError);
        var inputs = _mapper.Project(BuildChartWithImmunizations(imm));
        inputs.Immunizations.Should().ContainSingle().Which.Status.Should().Be("entered-in-error");
    }

    [Theory]
    [InlineData("140", "influenza")]
    [InlineData("115", "tdap")]
    [InlineData("187", "zoster_recombinant")]
    [InlineData("215", "pneumococcal_pcv20")]
    [InlineData("207", "covid19")]
    [InlineData("99999", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeVaccine_Maps_Common_Cvx_Codes(string? input, string expected)
    {
        FhirToFactMapper.NormalizeVaccine(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("2093-3", "total_cholesterol", 215.0, "mg/dL")]
    [InlineData("2085-9", "hdl_cholesterol", 52.0, "mg/dL")]
    [InlineData("13457-7", "ldl_cholesterol_calculated", 140.0, "mg/dL")]
    public void Lipid_Loincs_Project_To_Expected_Engine_Names(string loinc, string expectedName, double value, string unit)
    {
        var obs = new Observation
        {
            Code = new CodeableConcept { Coding = new List<Coding> { new("http://loinc.org", loinc) } },
            Value = new Quantity { Value = (decimal)value, Unit = unit },
            Effective = new FhirDateTime("2025-09-01"),
        };
        var chart = new PatientChart(
            Patient: BuildPatient(),
            Conditions: Array.Empty<Condition>(),
            Observations: new[] { obs },
            MedicationRequests: Array.Empty<MedicationRequest>(),
            Allergies: Array.Empty<AllergyIntolerance>(),
            Immunizations: Array.Empty<Hl7.Fhir.Model.Immunization>(),
            Procedures: Array.Empty<Hl7.Fhir.Model.Procedure>(),
            Encounter: null);

        var inputs = _mapper.Project(chart);
        var lab = inputs.Labs.Should().ContainSingle().Subject;
        lab.Name.Should().Be(expectedName);
        lab.Value.Should().Be(value);
    }

    [Fact]
    public void Observation_With_Bp_Components_Projects_Both_Systolic_And_Diastolic()
    {
        var bp = new Observation
        {
            Code = new CodeableConcept { Text = "Blood pressure" },
            Effective = new FhirDateTime("2025-09-15"),
            Component = new List<Observation.ComponentComponent>
            {
                new()
                {
                    Code = new CodeableConcept { Coding = new List<Coding> { new("http://loinc.org", "8480-6") } },
                    Value = new Quantity { Value = 142m, Unit = "mmHg" },
                },
                new()
                {
                    Code = new CodeableConcept { Coding = new List<Coding> { new("http://loinc.org", "8462-4") } },
                    Value = new Quantity { Value = 88m, Unit = "mmHg" },
                },
            },
        };
        var chart = new PatientChart(
            Patient: BuildPatient(),
            Conditions: Array.Empty<Condition>(),
            Observations: new[] { bp },
            MedicationRequests: Array.Empty<MedicationRequest>(),
            Allergies: Array.Empty<AllergyIntolerance>(),
            Immunizations: Array.Empty<Hl7.Fhir.Model.Immunization>(),
            Procedures: Array.Empty<Hl7.Fhir.Model.Procedure>(),
            Encounter: null);

        var inputs = _mapper.Project(chart);
        inputs.Labs.Should().HaveCount(2);
        inputs.Labs.Single(l => l.Name == "systolic_bp").Value.Should().Be(142);
        inputs.Labs.Single(l => l.Name == "diastolic_bp").Value.Should().Be(88);
    }

    [Fact]
    public void Condition_Projects_Onset_And_Recorded_Date()
    {
        var condition = new Condition
        {
            Code = new CodeableConcept { Coding = new List<Coding> { new("http://snomed.info/sct", "73211009") { Display = "Diabetes mellitus" } } },
            ClinicalStatus = new CodeableConcept { Coding = new List<Coding> { new("http://terminology.hl7.org/CodeSystem/condition-clinical", "active") } },
            Onset = new FhirDateTime("2005-09-20"),
            RecordedDate = "2019-05-28",
        };
        var chart = new PatientChart(
            Patient: BuildPatient(),
            Conditions: new[] { condition },
            Observations: Array.Empty<Observation>(),
            MedicationRequests: Array.Empty<MedicationRequest>(),
            Allergies: Array.Empty<AllergyIntolerance>(),
            Immunizations: Array.Empty<Hl7.Fhir.Model.Immunization>(),
            Procedures: Array.Empty<Hl7.Fhir.Model.Procedure>(),
            Encounter: null);

        var projected = _mapper.Project(chart).Conditions.Should().ContainSingle().Subject;
        projected.Onset.Should().Be(new DateTimeOffset(2005, 9, 20, 0, 0, 0, TimeSpan.Zero));
        projected.RecordedDate.Should().Be(new DateTimeOffset(2019, 5, 28, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Medication_From_Reference_Display_Projects_When_CodeableConcept_Absent()
    {
        // Epic returns medicationReference (with a display), not medicationCodeableConcept.
        var req = new MedicationRequest
        {
            Status = MedicationRequest.MedicationrequestStatus.Active,
            Medication = new ResourceReference { Display = "Lisinopril 20 MG tablet" },
        };
        var chart = new PatientChart(
            Patient: BuildPatient(),
            Conditions: Array.Empty<Condition>(),
            Observations: Array.Empty<Observation>(),
            MedicationRequests: new[] { req },
            Allergies: Array.Empty<AllergyIntolerance>(),
            Immunizations: Array.Empty<Hl7.Fhir.Model.Immunization>(),
            Procedures: Array.Empty<Hl7.Fhir.Model.Procedure>(),
            Encounter: null);

        var med = _mapper.Project(chart).Medications.Should().ContainSingle().Subject;
        med.Name.Should().Be("lisinopril", because: "the name comes from medicationReference.display when no codeable concept is present");
        med.Active.Should().BeTrue();
    }

    [Theory]
    [InlineData("Influenza, seasonal", "influenza")]
    [InlineData("Tdap booster", "tdap")]
    [InlineData("Shingrix recombinant zoster", "zoster_recombinant")]
    [InlineData("Pneumococcal PCV20", "pneumococcal_pcv20")]
    [InlineData("COVID-19 mRNA vaccine", "covid19")]
    [InlineData("Random unknown vaccine", "")]
    public void NormalizeVaccine_Maps_Display_Text(string input, string expected)
    {
        FhirToFactMapper.NormalizeVaccine(input).Should().Be(expected);
    }

    // -------- Procedure projection --------

    private static Hl7.Fhir.Model.Procedure BuildProcedure(
        string? cptCode = "77067",
        string? cptSystem = "http://www.ama-assn.org/go/cpt",
        string? displayText = null,
        string? performedIso = "2024-09-15",
        EventStatus status = EventStatus.Completed)
    {
        var proc = new Hl7.Fhir.Model.Procedure { Status = status };
        if (cptCode is not null || displayText is not null)
        {
            proc.Code = new CodeableConcept
            {
                Coding = cptCode is null ? null : new List<Coding>
                {
                    new(cptSystem ?? "", cptCode),
                },
                Text = displayText,
            };
        }
        if (performedIso is not null)
        {
            proc.Performed = new FhirDateTime(performedIso);
        }
        return proc;
    }

    [Theory]
    [InlineData("77067", "mammography")]
    [InlineData("77065", "mammography")]
    [InlineData("45378", "colonoscopy")]
    [InlineData("45385", "colonoscopy")]
    [InlineData("45330", "sigmoidoscopy")]
    [InlineData("82270", "fit_screening")]
    [InlineData("88142", "cervical_cytology")]
    [InlineData("77080", "dxa_scan")]
    public void Procedure_Cpt_Codes_Map_To_Canonical_Names(string cpt, string expected)
    {
        var inputs = _mapper.Project(BuildChartWithProcedures(BuildProcedure(cptCode: cpt)));
        inputs.Procedures.Should().ContainSingle().Which.Kind.Should().Be(expected);
    }

    [Theory]
    [InlineData("Screening mammography, bilateral", "mammography")]
    [InlineData("Colonoscopy with polypectomy", "colonoscopy")]
    [InlineData("Flexible sigmoidoscopy", "sigmoidoscopy")]
    [InlineData("Fecal immunochemical test", "fit_screening")]
    [InlineData("Pap smear (cervical cytology)", "cervical_cytology")]
    [InlineData("DEXA bone density", "dxa_scan")]
    public void Procedure_Display_Text_Falls_Back_When_No_Code(string display, string expected)
    {
        var inputs = _mapper.Project(BuildChartWithProcedures(BuildProcedure(cptCode: null, displayText: display)));
        inputs.Procedures.Should().ContainSingle().Which.Kind.Should().Be(expected);
    }

    [Fact]
    public void Procedure_Unknown_Code_And_Unknown_Display_Is_Dropped()
    {
        var inputs = _mapper.Project(BuildChartWithProcedures(BuildProcedure(cptCode: "99999", displayText: "Random procedure")));
        inputs.Procedures.Should().BeEmpty();
    }

    [Fact]
    public void Procedure_Missing_Performed_Date_Is_Dropped()
    {
        var inputs = _mapper.Project(BuildChartWithProcedures(BuildProcedure(performedIso: null)));
        inputs.Procedures.Should().BeEmpty();
    }

    [Theory]
    [InlineData(EventStatus.Completed, "completed")]
    [InlineData(EventStatus.InProgress, "in-progress")]
    [InlineData(EventStatus.NotDone, "not-done")]
    [InlineData(EventStatus.EnteredInError, "entered-in-error")]
    [InlineData(EventStatus.Stopped, "stopped")]
    [InlineData(EventStatus.OnHold, "on-hold")]
    [InlineData(EventStatus.Preparation, "preparation")]
    [InlineData(EventStatus.Unknown, "unknown")]
    public void Procedure_Status_Maps_To_Engine_String(EventStatus fhir, string expected)
    {
        var inputs = _mapper.Project(BuildChartWithProcedures(BuildProcedure(status: fhir)));
        inputs.Procedures.Should().ContainSingle().Which.Status.Should().Be(expected);
    }

    [Fact]
    public void Procedure_Cpt_Code_Without_System_Uses_Bare_Code_Fallback()
    {
        // Cerner and some other EHRs return Procedure.Code.Coding entries with
        // the system field empty when the local CPT catalog is the implicit
        // source. Our ProcedureCodeToCanonical table has bare-code keys
        // ("|45378") for this case; the dispatch must fall back to them.
        var inputs = _mapper.Project(BuildChartWithProcedures(
            BuildProcedure(cptCode: "45378", cptSystem: "")));
        inputs.Procedures.Should().ContainSingle().Which.Kind.Should().Be("colonoscopy");
    }
}
