using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Net.Http;
using Alexa.NET.Request;
using Alexa.NET.Response;
using Alexa.NET.Request.Type;
using Alexa.NET;
using System.Net.Http.Headers;
using System.Configuration;
using Microsoft.Extensions.Configuration;
using System.Text;
using System.Net;
using System.Collections.Generic;
using System.Linq;
namespace PowerRocksFunction
{
    public class PowerRocksFunction
    {
        private IConfigurationRoot _config;
        private const decimal ValorTarifaPonta = 0.83916m;
        private const decimal ValorTarifaForaPonta = 0.39765m;
        private const decimal ValorTarifaIntermediario = 0.53394m;

        [FunctionName("PowerRocksFunc")]
        public async Task<SkillResponse> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
            ILogger log, ExecutionContext context)
        {
            _config = new ConfigurationBuilder()
                .SetBasePath(context.FunctionAppDirectory)
                .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();
            log.LogInformation("Iniciou o Request da Alexa");
            string json = await req.ReadAsStringAsync();
            var skillRequest = JsonConvert.DeserializeObject<SkillRequest>(json);
            return await ProcessRequest(skillRequest);
        }
        private async Task<SkillResponse> ProcessRequest(SkillRequest skillRequest)
        {
            var requestType = skillRequest.GetRequestType();
            SkillResponse response = null;
            if (requestType == typeof(LaunchRequest))
            {
                response = await LaunchPowerRock();
            }
            else if (requestType == typeof(IntentRequest))
            {
                response = await GetIntent(skillRequest, response);
            }
            else if (requestType == typeof(SessionEndedRequest))
            {
                response = FinallyAlexaSession();
            }
            return response;
        }
        private async Task<SkillResponse> LaunchPowerRock()
        {
            try
            {
                var usuario = await ObterDadosUsuario();
                var response = ResponseBuilder.Tell($"Bem vindo ao PowerRocks {usuario.FullName}. Você quer saber seus dados de consumo do dia; ou do mês?");
                response.Response.ShouldEndSession = false;
                return response;
            }
            catch
            {
                var speech = new SsmlOutputSpeech
                {
                    Ssml = $"<speak>Ouve um erro ao solicitar seu consumo. <break time='0.5s'/> Tente novamente por favor</speak>"
                };
                var response = ResponseBuilder.Tell(speech);
                response.Response.ShouldEndSession = false;
                return response;
            }
        }
        private SkillResponse FinallyAlexaSession()
        {
            var speech = new SsmlOutputSpeech
            {
                Ssml = $"<speak>Até mais Rocker</speak>"
            };
            var response = ResponseBuilder.TellWithCard(speech, "Até mais Rocker", "PowerRock");
            response.Response.ShouldEndSession = true;
            return response;
        }
        private async Task<SkillResponse> GetIntent(SkillRequest skillRequest, SkillResponse response)
        {
            var intentRequest = skillRequest.Request as IntentRequest;
            if (intentRequest.Intent.Name == "PeriodoIntent")
                response = await PeriodoIntent(intentRequest.Intent, response);
            else if (intentRequest.Intent.Name == "ContinuarIntent")
                response = await PeriodoIntent(intentRequest.Intent, response);
            return response;
        }
        private async Task<SkillResponse> PeriodoIntent(Intent intent, SkillResponse response)
        {
            try
            {
                var periodo = intent.Slots["Periodo"].Value;
                var periodoParse = DateTime.Parse(periodo, null, System.Globalization.DateTimeStyles.RoundtripKind);

                var inicio = DateTime.Now;
                var fim = inicio;

                var primerDiaDoMes = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);

                if (periodoParse == primerDiaDoMes)
                    inicio = periodoParse;

                var consumos = await ObterDadosCalculados(inicio, fim);

                var consumoTotalKwh = consumos.Sum(con => con.KW);
                var consumoTotalReais = consumos.Sum(con => con.Reais);

                var mensagemMediaDiaria = string.Empty;

                if (inicio.Date == fim.Date)
                    mensagemMediaDiaria = await CriarMensagemMediaDiaria(consumoTotalKwh);

                var speech = new SsmlOutputSpeech
                {
                    Ssml = $"<speak>Ummm, detectei que você está conectado na CELESC. " +
                    $"Vou calcular seu consumo em reais; " +
                    $"um momento por favor. <break time='0.5s'/>" +
                    $"Seu consumo é de {consumoTotalKwh:N0} kilo watts hora no valor de {consumoTotalReais:N2} reais. { mensagemMediaDiaria } " +
                    $"</speak>"
                };

                response = ResponseBuilder.Tell(speech);
                response.Response.ShouldEndSession = false;
                return response;
            }
            catch
            {
                var speech = new SsmlOutputSpeech
                {
                    Ssml = $"<speak>Não reconheci o que você falou. <break time='0.5s'/> Você pode repetir por favor</speak>"
                };
                response = ResponseBuilder.Tell(speech);
                response.Response.ShouldEndSession = false;
                return response;
            }
        }
        private async Task<string> CriarMensagemMediaDiaria(decimal consumoDiaKw)
        {
            var mediasDiarias = await OberMediaDiariaMensal();

            if (mediasDiarias > consumoDiaKw)
                return $"Você está economizando mais que seu consumo médio";
            else
                return $"Você está gastando mais que seu consumo médio";
        }

        private async Task<Usuario> ObterDadosUsuario()
        {
            using var client = new HttpClient();
            var autenticacao = await Autenticar(client);

            if (autenticacao != null)
            {
                var url = _config["url"];
                var subscriptionId = _config["subscriptionId"];
                var userId = _config["userId"];

                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", autenticacao.Token);
                var response = await client.GetAsync($"{url}{subscriptionId}/users/{userId}");

                var conteudo = await response.Content.ReadAsStringAsync();

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var usuario = JsonConvert.DeserializeObject<Usuario>(conteudo);
                    if (usuario != null)
                        return usuario;
                }
            }
            return null;
        }

        private async Task<IEnumerable<MeasurementsKind>> ObterMeasurements(DateTime inicio, DateTime fim)
        {
            using var client = new HttpClient();
            var autenticacao = await Autenticar(client);

            if (autenticacao != null)
            {
                var url = _config["url"];
                var subscriptionId = _config["subscriptionId"];
                var sdpId = _config["sdpId"];

                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", autenticacao.Token);

                var result = await client.GetAsync($"{url}{subscriptionId}/sdps/{sdpId}/measurements?commaSeparatedMeasurements=ActiveEnergy&dayStart={inicio:yyyy-MM-dd}&dayEnd={fim:yyyy-MM-dd}");

                var conteudo = await result.Content.ReadAsStringAsync();

                if (result.StatusCode == HttpStatusCode.OK)
                    return JsonConvert.DeserializeObject<IEnumerable<MeasurementsKind>>(conteudo);
            }
            return null;
        }

        private async Task<IEnumerable<ConsumoCalculado>> ObterDadosCalculados(DateTime inicio, DateTime fim)
        {
            var measurementsKinds = await ObterMeasurements(inicio, fim);
            var measurements = measurementsKinds.FirstOrDefault().Measurements;

            var groupby = measurements.GroupBy(ms => ms.TimeOfUse);

            var consumosCalculados = new List<ConsumoCalculado>
            {
                MontarConsumoCalculado(groupby, TimeOfUseEnum.HorarioPonta),
                MontarConsumoCalculado(groupby, TimeOfUseEnum.HorarioForaPonta),
                MontarConsumoCalculado(groupby, TimeOfUseEnum.Intermediario)
            };

            return consumosCalculados;
        }

        private ConsumoCalculado MontarConsumoCalculado(IEnumerable<IGrouping<TimeOfUseEnum, Measurements>> groupby, TimeOfUseEnum tipo)
        {
            var consumosCalculados = new List<ConsumoCalculado>();

            var consumoKwh = groupby.Where(gr => gr.Key == tipo).SelectMany(m => m).Sum(m => m.Value ?? 0);
            var consumoReais = CalcularValorTarifa(tipo, consumoKwh);

            return new ConsumoCalculado
            {
                TimeOfUseEnum = tipo,
                KW = consumoKwh,
                Reais = consumoReais
            };
        }

        private async Task<decimal> OberMediaDiariaMensal()
        {
            var dados30Dias = await ObterDadosCalculados(DateTime.Now.AddDays(-30), DateTime.Now);

            var consumoTotalKwh = dados30Dias.Sum(con => con.KW);

            return consumoTotalKwh / 30;
        }

        private decimal CalcularValorTarifa(TimeOfUseEnum timeOfUse, decimal totalKw)
        {
            switch (timeOfUse)
            {
                case TimeOfUseEnum.HorarioPonta:
                    return totalKw * ValorTarifaPonta;
                case TimeOfUseEnum.HorarioForaPonta:
                    return totalKw * ValorTarifaForaPonta;
                case TimeOfUseEnum.Intermediario:
                    return totalKw * ValorTarifaIntermediario;
                default:
                    return 0;
            }
        }

        private async Task<Autenticacao> Autenticar(HttpClient client)
        {
            var url = _config["url"];
            var usuario = _config["usuario"];
            var senha = _config["senha"];

            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var respToken = await client.PostAsync(url + $"login?username={usuario}&password={senha}", null);
            var conteudo = await respToken.Content.ReadAsStringAsync();

            if (respToken.StatusCode == HttpStatusCode.OK)
            {
                var autenticacao = JsonConvert.DeserializeObject<Autenticacao>(conteudo);
                if (autenticacao.Token != null)
                    return autenticacao;
            }
            return null;
        }

        private class MeasurementsKind
        {
            public string MeasurementKind { get; set; }
            public IEnumerable<Measurements> Measurements { get; set; }
        }

        private class ConsumoCalculado
        {
            public TimeOfUseEnum TimeOfUseEnum { get; set; }
            public decimal KW { get; set; }
            public decimal Reais { get; set; }
        }

        private class Measurements
        {
            public DateTime DateTime { get; set; }
            public decimal? Value { get; set; }
            public int ToU { get; set; }
            public TimeOfUseEnum TimeOfUse { get; set; }
            public int ReactiveEnergyToU { get; set; }
            public int Quality { get; set; }
        }

        private enum TimeOfUseEnum { Desconhecido = 0, HorarioPonta = 1, HorarioForaPonta = 2, Intermediario = 3 }

        private class Autenticacao
        {
            public string Token { get; set; }
        }

        private class Usuario
        {
            public string FullName { get; set; }
        }

        private class TarifaBranca
        {
            public InfoTarifa Ponta { get; set; }
            public IEnumerable<InfoTarifa> ForaPonta { get; set; }
            public IEnumerable<InfoTarifa> Intermediario { get; set; }
        }

        private class InfoTarifa
        {
            public decimal Valor { get; set; }
            public TimeSpan Inicio { get; set; }
            public TimeSpan Fim { get; set; }
        }
    }
}