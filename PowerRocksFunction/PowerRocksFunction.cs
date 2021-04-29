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

namespace PowerRocksFunction
{
    public class PowerRocksFunction
    {
        private readonly HttpClient _client;
        public PowerRocksFunction(HttpClient client)
        {
            _client = client;
        }

        [FunctionName("PowerRocksFunc")]
        public async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("Iniciou o Request da Alexa");
            string json = await req.ReadAsStringAsync();
            var skillRequest = JsonConvert.DeserializeObject<SkillRequest>(json);
            return ProcessRequest(skillRequest);
            //Alexa identifica comando
            //Comando chama a API Azure functions
            //API recebe um objeto do tipo SkillRequest
            //Identifico SkillRequest qual a intenção
            //Executo comando da intenção
        }
        private IActionResult ProcessRequest(SkillRequest skillRequest)
        {
            var requestType = skillRequest.GetRequestType();
            SkillResponse response = null;
            if (requestType == typeof(LaunchRequest))
            {
                response = LaunchPowerRock();
            }
            else if (requestType == typeof(IntentRequest))
            {
                response = GetIntent(skillRequest, response);
            }
            else if (requestType == typeof(SessionEndedRequest))
            {
                response = FinallyAlexaSession();
            }
            return new OkObjectResult(response);
        }
        private SkillResponse LaunchPowerRock()
        {
            SkillResponse response = ResponseBuilder.Tell("Bem vindo ao PowerRocks. O que você deseja saber?");
            response.Response.ShouldEndSession = false;
            return response;
        }
        private SkillResponse FinallyAlexaSession()
        {
            SkillResponse response;
            var speech = new SsmlOutputSpeech();
            speech.Ssml = $"<speak>Até mais Rocker</speak>";
            response = ResponseBuilder.TellWithCard(speech, "Até mais Rocker", "PowerRock");
            response.Response.ShouldEndSession = true;
            return response;
        }
        private SkillResponse GetIntent(SkillRequest skillRequest, SkillResponse response)
        {
            var intentRequest = skillRequest.Request as IntentRequest;
            if (intentRequest.Intent.Name == "HorarioForaPonta")
            {
                response = IntentHorarioPonta(response);
            }
            return response;
        }
        private SkillResponse IntentHorarioPonta(SkillResponse response)
        {
            var speech = new SsmlOutputSpeech();
            speech.Ssml = $"<speak>Você esta no horário Fora Ponta</speak>";
            response = ResponseBuilder.TellWithCard(speech, "Sua resposta é: ", "O horário é Fora Ponta");
            response.Response.ShouldEndSession = false;
            return response;
        }
    }
}
