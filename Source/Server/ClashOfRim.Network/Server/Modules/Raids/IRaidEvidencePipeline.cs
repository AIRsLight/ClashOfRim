namespace AIRsLight.ClashOfRim.Network.Modules.Raids;

// Dispatches the existing post-upload extensions after domain validation.
internal interface IRaidEvidencePipeline
{
    void Run(RaidEvidenceSchedule evidence, RaidEvidenceSchedulePreparation? preparation);
}
