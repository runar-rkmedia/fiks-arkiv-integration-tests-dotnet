using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using KS.Fiks.Arkiv.Integration.Tests.FiksIO;
using KS.Fiks.Arkiv.Integration.Tests.Helpers;
using KS.Fiks.Arkiv.Integration.Tests.Library;
using KS.Fiks.Arkiv.Models.V1.Arkivstruktur;
using KS.Fiks.Arkiv.Models.V1.Innsyn.Hent.Mappe;
using KS.Fiks.Arkiv.Models.V1.Meldingstyper;
using KS.Fiks.Arkiv.Models.V1.Metadatakatalog;
using KS.Fiks.IO.Client;
using KS.Fiks.IO.Client.Models;
using KS.FiksProtokollValidator.Tests.IntegrationTests.Helpers;
using KS.FiksProtokollValidator.Tests.IntegrationTests.Validation;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace KS.Fiks.Arkiv.Integration.Tests.Tests.ArkivmeldingOppdatering
{
    /**
     * Disse testene sender først en arkivmelding for å opprette en mappe (evt med journalpost i mappe) for så
     * oppdatere mappen ved oppdater-meldinger, og til slutt hente mappen igjen vha enten referanseEksternNoekkel eller systemID
     * for å bekrefte at mappe er endret som ønsket
     */
    public class OppdaterOgHentMappeTests : IntegrationTestsBase
    {
        [SetUp]
        public async Task Setup()
        {
            //TODO En annen lokal lagring som kjørte for disse testene hadde vært stilig i stedet for en liste. 
            MottatMeldingArgsList = new List<MottattMeldingArgs>();
            var config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.Local.json")
                .Build();
            Client = await FiksIOClient.CreateAsync(FiksIOConfigurationBuilder.CreateFiksIOConfiguration(config));
            Client.NewSubscription(OnMottattMelding);
            FiksRequestService = new FiksRequestMessageService(config);
            MottakerKontoId = Guid.Parse(config["TestConfig:ArkivAccountId"]);
        }

        [Test]
        public async Task Oppdater_Saksmappe_Ansvarlig()
        {
            // Denne id'en gjør at Arkiv-simulatoren ser hvilke meldinger som henger sammen. Har ingen funksjon ellers.  
            var testSessionId = Guid.NewGuid().ToString();

            var referanseEksternNoekkel = new EksternNoekkel()
            {
                Fagsystem = "Fiks arkiv integrasjonstest for arkivmeldingoppdatering på saksmappe med eksternnoekkel",
                Noekkel = Guid.NewGuid().ToString()
            };

            Console.Out.WriteLine(
                $"Sender arkivmelding med ny saksmappe med EksternNoekkel fagsystem {referanseEksternNoekkel.Fagsystem} og noekkel {referanseEksternNoekkel.Noekkel}");
            
            // STEG 1: Opprett arkivmelding med en saksmappe og send inn
            var arkivmelding = MeldingGenerator.CreateArkivmeldingMedSaksmappe(referanseEksternNoekkel);

            var nySaksmappeAsSerialized = SerializeHelper.Serialize(arkivmelding);
            var validator = new SimpleXsdValidator();
            
            // Valider arkivmelding
            validator.Validate(nySaksmappeAsSerialized);

            // Send melding
            var nySaksmappeMeldingId = await FiksRequestService.Send(MottakerKontoId, FiksArkivMeldingtype.ArkivmeldingOpprett, nySaksmappeAsSerialized, "arkivmelding.xml", null, testSessionId);
            
            // Vent på 2 første response meldinger (mottatt og kvittering)
            VentPaSvar(2, 10);
            Assert.True(MottatMeldingArgsList.Count > 0, "Fikk ikke noen meldinger innen timeout");

            // Verifiser at man får mottatt melding
            SjekkForventetMelding(MottatMeldingArgsList, nySaksmappeMeldingId, FiksArkivMeldingtype.ArkivmeldingOpprettMottatt);

            // Verifiser at man får arkivmeldingKvittering melding
            SjekkForventetMelding(MottatMeldingArgsList, nySaksmappeMeldingId, FiksArkivMeldingtype.ArkivmeldingOpprettKvittering);
            
            // Hent meldingen
            var arkivmeldingKvitteringMelding = GetMottattMelding(MottatMeldingArgsList, nySaksmappeMeldingId, FiksArkivMeldingtype.ArkivmeldingOpprettKvittering);
            
            var arkivmeldingKvitteringPayload = MeldingHelper.GetDecryptedMessagePayload(arkivmeldingKvitteringMelding).Result;
            Assert.True(arkivmeldingKvitteringPayload.Filename == "arkivmelding-kvittering.xml", "Filnavn ikke som forventet arkivmelding-kvittering.xml");
    
            // Valider innhold (xml)
            validator.Validate(arkivmeldingKvitteringPayload.PayloadAsString);

            // STEG 2: Send oppdatering av saksmappe og saksansvarlig
            var nySaksansvarlig = "Nelly Ny Saksansvarlig";
            var arkivmeldingOppdatering = MeldingGenerator.CreateArkivmeldingOppdateringSaksmappeOppdateringNySaksansvarlig(referanseEksternNoekkel, nySaksansvarlig);
            
            var arkivmeldingOppdateringSerialized = SerializeHelper.Serialize(arkivmeldingOppdatering);
            
            // Valider innhold (xml)
            validator.Validate(arkivmeldingOppdateringSerialized);
            
            // Nullstill meldingsliste
            MottatMeldingArgsList.Clear();
            
            // Send oppdater melding
            var arkivmeldingOppdaterMeldingId = await FiksRequestService.Send(MottakerKontoId, FiksArkivMeldingtype.ArkivmeldingOppdater, arkivmeldingOppdateringSerialized, "arkivmelding.xml", null, testSessionId);

            // Vent på 2 respons meldinger. Mottat og kvittering 
            VentPaSvar(2, 10);
            Assert.True(MottatMeldingArgsList.Count > 0, "Fikk ikke noen meldinger innen timeout");
            
            // Verifiser at man får mottatt melding
            SjekkForventetMelding(MottatMeldingArgsList, arkivmeldingOppdaterMeldingId, FiksArkivMeldingtype.ArkivmeldingOppdaterMottatt);

            // Verifiser at man får arkivmeldingOppdaterKvittering melding
            SjekkForventetMelding(MottatMeldingArgsList, arkivmeldingOppdaterMeldingId, FiksArkivMeldingtype.ArkivmeldingOppdaterKvittering);
            
            // Hent melding
            var arkivmeldingOppdaterKvittering = GetMottattMelding(MottatMeldingArgsList, arkivmeldingOppdaterMeldingId, FiksArkivMeldingtype.ArkivmeldingOppdaterKvittering);

            Assert.IsNotNull(arkivmeldingOppdaterKvittering);
            
            // STEG 3: Henting av saksmappe
            var mappeHent = MeldingGenerator.CreateMappeHent(referanseEksternNoekkel);
            
            var mappeHentSerialized = SerializeHelper.Serialize(mappeHent);
            
            // Utkommenter dette hvis man vil å skrive til fil for å sjekke resultat manuelt
            //File.WriteAllText("MappeHent.xml", mappeHentSerialized);
            
            // Valider innhold (xml)
            validator.Validate(mappeHentSerialized);
            
            // Nullstill meldingsliste
            MottatMeldingArgsList.Clear();
            
            // Send hent melding
            var mappeHentMeldingId = await FiksRequestService.Send(MottakerKontoId, FiksArkivMeldingtype.MappeHent, mappeHentSerialized, "arkivmelding.xml", null, testSessionId);

            // Vent på 1 respons meldinger 
            VentPaSvar(1, 10);

            Assert.True(MottatMeldingArgsList.Count > 0, "Fikk ikke noen meldinger innen timeout");
            
            // Verifiser at man får mappeHentResultat melding
            SjekkForventetMelding(MottatMeldingArgsList, mappeHentMeldingId, FiksArkivMeldingtype.MappeHentResultat);
            
            // Hent melding
            var mappeHentResultatMelding = GetMottattMelding(MottatMeldingArgsList, mappeHentMeldingId, FiksArkivMeldingtype.MappeHentResultat);

            Assert.IsNotNull(mappeHentResultatMelding);
            
            var mappeHentResultatPayload = MeldingHelper.GetDecryptedMessagePayload(mappeHentResultatMelding).Result;
            
            // Valider innhold (xml)
            validator.Validate(mappeHentResultatPayload.PayloadAsString);

            var mappeHentResultat = SerializeHelper.DeserializeXml<MappeHentResultat>(mappeHentResultatPayload.PayloadAsString);

            var saksmappe = (Saksmappe)mappeHentResultat.Mappe;

            Assert.AreEqual(nySaksansvarlig, saksmappe.Saksansvarlig.Navn);
        }
    }
}