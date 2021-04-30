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

                var response = ResponseBuilder.Tell($"Bem vindo ao PowerRocks {usuario.FullName}. Você quer saber seus dados de consumo de hoje ou do mês?");
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
                var fim = DateTime.Now;

                var primerDiaDoMes = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);

                if (periodoParse == primerDiaDoMes)
                    inicio = periodoParse;

                var measurementsKinds = await ObterMeasurements(inicio, fim);
                var measurements = measurementsKinds.FirstOrDefault().Measurements;

                var consumoKwh = decimal.Zero;
                var valorReais = decimal.Zero;

                foreach (var measurement in measurements)
                {
                    consumoKwh += measurement.Value ?? 0;

                    var intervalo = new TimeSpan(measurement.DateTime.Hour, measurement.DateTime.Minute, measurement.DateTime.Second);

                    valorReais += CalcularValorTarifa(measurement);

                    //Somar todas Pontas  * Tarifa Ponta
                    //Somar todas fora ponta
                    //Somar todo Intermediario
                }

                var speech = new SsmlOutputSpeech
                {
                    Ssml = $"<speak>Ummm, detectei que você esta conectado na CELESC. <break time='0.5s'/>" +
                    $"Vou calcular seu consumo em reais. <break time='0.5s'/>" +
                    $"Um momento por favor. <break time='1s'/>" +
                    $"Seu consumo é de {consumoKwh:N0} kilo watts hora no valor de {valorReais:N2} reais" +
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

        private decimal CalcularValorTarifa(Measurements measurement)
        {
            var tarifaBranca = Tarifa();

            var intervalo = new TimeSpan(measurement.DateTime.Hour, measurement.DateTime.Minute, measurement.DateTime.Second);

            var valorReais = decimal.Zero;

            if(intervalo.TotalSeconds >= tarifaBranca.Ponta.Inicio.TotalSeconds && intervalo.TotalSeconds <= tarifaBranca.Ponta.Fim.TotalSeconds)
                valorReais = measurement.Value ?? 0;
            if (intervalo.TotalSeconds >= tarifaBranca.ForaPonta.ToArray()[0].Inicio.TotalSeconds && intervalo.TotalSeconds <= tarifaBranca.ForaPonta.ToArray()[0].Fim.TotalSeconds)
                valorReais = measurement.Value ?? 0;
            if (intervalo.TotalSeconds >= tarifaBranca.ForaPonta.ToArray()[1].Inicio.TotalSeconds && intervalo.TotalSeconds <= tarifaBranca.ForaPonta.ToArray()[1].Fim.TotalSeconds)
                valorReais = measurement.Value ?? 0;
            if (intervalo.TotalSeconds >= tarifaBranca.Intermediario.ToArray()[0].Inicio.TotalSeconds && intervalo.TotalSeconds <= tarifaBranca.Intermediario.ToArray()[0].Fim.TotalSeconds)
                valorReais = measurement.Value ?? 0;
            if (intervalo.TotalSeconds >= tarifaBranca.Intermediario.ToArray()[1].Inicio.TotalSeconds && intervalo.TotalSeconds <= tarifaBranca.Intermediario.ToArray()[1].Fim.TotalSeconds)
                valorReais = measurement.Value ?? 0;

            return valorReais;
        }

        private TarifaBranca Tarifa() => new TarifaBranca
        {
            Ponta = new InfoTarifa()
            {
                Valor = decimal.Parse("0.83916"),
                Inicio = new TimeSpan(18, 45, 00),
                Fim = new TimeSpan(21, 30, 00)
            },
            ForaPonta = new List<InfoTarifa>() {
                new InfoTarifa()
                {
                    Valor = decimal.Parse("0.39765"),
                    Inicio = new TimeSpan(00, 00, 00),
                    Fim = new TimeSpan(17, 30, 00)
                },
                new InfoTarifa()
                {
                    Valor = decimal.Parse("0.39765"),
                    Inicio = new TimeSpan(22, 45, 00),
                    Fim = new TimeSpan(23, 45, 00)
                }
            },
            Intermediario = new List<InfoTarifa>()
            {
                new InfoTarifa()
                {
                    Valor = decimal.Parse("0.53394"),
                    Inicio = new TimeSpan(17, 45, 00),
                    Fim = new TimeSpan(18, 30, 00)
                },
                new InfoTarifa()
                {
                    Valor = decimal.Parse("0.53394"),
                    Inicio = new TimeSpan(21, 30, 00),
                    Fim = new TimeSpan(22, 30, 00)
                }
            }
        };


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
