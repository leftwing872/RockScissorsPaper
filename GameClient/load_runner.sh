!/bin/bash

for i in {1..150}
do
  ./GameClient &
  sleep 4
done
