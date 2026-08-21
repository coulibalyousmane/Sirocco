# Aucune image Docker officielle largement adoptee pour Gatling OSS (contrairement a
# grafana/k6) : cette image telecharge le bundle OSS directement depuis Maven Central sur une
# base JDK. Constat reel important : depuis la version 3.15.x, le bundle OSS n'est plus un
# script bin/gatling.sh autonome mais un squelette de projet Maven (pom.xml + mvnw +
# src/test/java), pilote par le gatling-maven-plugin — d'ou l'usage de mvnw ci-dessous plutot
# qu'un appel direct a gatling.sh. Voir benchmark/README.md pour la commande d'usage complete.

FROM eclipse-temurin:21-jdk

ARG GATLING_VERSION=3.15.1

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl unzip \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /opt
RUN curl -fsSL -o gatling.zip \
      "https://repo1.maven.org/maven2/io/gatling/highcharts/gatling-charts-highcharts-bundle/${GATLING_VERSION}/gatling-charts-highcharts-bundle-${GATLING_VERSION}-bundle.zip" \
    && unzip -q gatling.zip \
    && rm gatling.zip \
    && mv "gatling-charts-highcharts-bundle-${GATLING_VERSION}" gatling \
    && rm -f gatling/src/test/java/example/BasicSimulation.java

WORKDIR /opt/gatling
ENTRYPOINT ["./mvnw", "-B", "-q", "gatling:test"]
