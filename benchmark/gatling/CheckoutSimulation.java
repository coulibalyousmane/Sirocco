// Simulation Gatling du benchmark comparatif (voir benchmark/README.md) : meme sequence exacte
// que benchmark/scenarios/sirocco-checkout.yaml et benchmark/k6/checkout.js (login puis
// checkout, meme panier), meme rampe 20 -> 150 utilisateurs/s sur 90s. injectOpen (via
// rampUsersPerSec) est le modele ouvert de Gatling, l'equivalent direct de --from-rps/--to-rps
// de Sirocco — un utilisateur Gatling ici ne fait qu'une seule iteration (login+checkout), donc
// "utilisateurs/s" et "iterations/s" coincident, comme pour les trois autres outils.
//
// En Java DSL (pas Scala) : le bundle OSS Gatling 3.15.x livre un squelette Maven configure
// pour compiler du Java (voir benchmark/gatling/Dockerfile), sans plugin Scala.
//
// Le profil est parametrable par l'environnement, avec pour defaut les valeurs du benchmark publie :
// benchmark/run.sh n'en passe aucune et reproduit donc exactement le tir de results/RESULTS.md,
// tandis que benchmark/saturation.sh les surcharge. START_RATE == TARGET_RATE donne un debit
// constant (rampUsersPerSec(n).to(n) est plat), sans introduire un second injecteur ici.
//
// Asymetrie assumee et exploitee par l'article sur la dette d'ordonnancement : injectOpen n'a
// AUCUN plafond d'utilisateurs virtuels, contrairement au maxVUs de k6 ou au --max-vus de Sirocco.
// Gatling cree autant d'utilisateurs que le debit l'exige ; il n'a donc jamais de file d'attente
// interne a signaler, et son attente apparait dans la latence qu'il rapporte.

import io.gatling.javaapi.core.*;
import io.gatling.javaapi.http.*;

import java.time.Duration;

import static io.gatling.javaapi.core.CoreDsl.*;
import static io.gatling.javaapi.http.HttpDsl.*;

public class CheckoutSimulation extends Simulation {

  private final String targetUrl = System.getenv().getOrDefault("TARGET_URL", "http://localhost:5281");

  private final double startRate = Double.parseDouble(System.getenv().getOrDefault("START_RATE", "20"));
  private final double targetRate = Double.parseDouble(System.getenv().getOrDefault("TARGET_RATE", "150"));
  private final long durationSeconds = Long.parseLong(System.getenv().getOrDefault("DURATION_SECONDS", "90"));

  private final HttpProtocolBuilder httpProtocol = http.baseUrl(targetUrl);

  private final ScenarioBuilder checkoutScenario = scenario("benchmark-checkout")
      .exec(
          http("login")
              .post("/api/auth/login")
              .body(StringBody("{\"username\":\"demo\",\"password\":\"demo\"}")).asJson()
              .check(jsonPath("$.token").saveAs("token"))
      )
      .exec(
          http("checkout")
              .post("/api/checkout")
              .header("Authorization", "Bearer #{token}")
              .body(StringBody("{\"items\":[{\"productId\":1,\"quantity\":2}]}")).asJson()
      );

  {
    setUp(
        checkoutScenario.injectOpen(
            rampUsersPerSec(startRate).to(targetRate).during(Duration.ofSeconds(durationSeconds)))
    ).protocols(httpProtocol);
  }
}
