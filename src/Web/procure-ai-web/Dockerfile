# The Angular app (plan 07): production build served by unprivileged nginx on 8080. Build context: this folder.
#   docker build -t procure-ai-web:<tag> src/Web/procure-ai-web
# One build runs in every environment: /config.json (public/config.json holds the local defaults) is replaced at run time by a file the
# chart mounts at /usr/share/nginx/html/config.json. Nothing environment-specific is baked into the image.
FROM node:26.13.0-alpine AS build
WORKDIR /src
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . .
RUN npx ng build --configuration production

FROM nginxinc/nginx-unprivileged:1.30.6-alpine-slim
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /src/dist/procure-ai-web/browser/ /usr/share/nginx/html/
EXPOSE 8080
