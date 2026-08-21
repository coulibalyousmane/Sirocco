// Simulation Gatling du benchmark comparatif (voir benchmark/README.md) : meme sequence exacte
// que benchmark/scenarios/tempest-checkout.yaml et benchmark/k6/checkout.js (login puis
// checkout, meme panier), meme rampe 20 -> 150 utilisateurs/s sur 90s. injectOpen (via
// rampUsersPerSec) est le modele ouvert de Gatling, l'equivalent direct de --from-rps/--to-rps
// de Tempest — un utilisateur Gatling ici ne fait qu'une seule iteration (login+checkout), donc
// "utilisateurs/s" et "iterations/s" coincident, comme pour les trois autres outils.
//
// En Java DSL (pas Scala) : le bundle OSS Gatling 3.15.x livre un squelette Maven configure
// pour compiler du Java (voir benchmark/gatling/Dockerfile), sans plugin Scala.

import io.gatling.javaapi.core.*;
import io.gatling.javaapi.http.*;

import java.time.Duration;

import static io.gatling.javaapi.core.CoreDsl.*;
import static io.gatling.javaapi.http.HttpDsl.*;

public class CheckoutSimulation extends Simulation {

  private final String targetUrl = System.getenv().getOrDefault("TARGET_URL", "http://localhost:5281");

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
        checkoutScenario.injectOpen(rampUsersPerSec(20).to(150).during(Duration.ofSeconds(90)))
    ).protocols(httpProtocol);
  }
}
